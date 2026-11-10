using System.Text.Json;
using Glazion.Api.Data;
using Glazion.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Glazion.Api.Infrastructure;

/// <summary>Writes an image's display and thumbnail WebP derivatives and records its pixel metrics on the asset.</summary>
public static class ImageDerivatives
{
    public const string UndecodableReason = "undecodable_image";
    public const string SkippedTooLargeReason = "derivatives_skipped_too_large";
    public const string ContentType = "image/webp";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Decodes <paramref name="original"/>, uploads both derivatives (fixed keys, so a retry overwrites), and sets the
    /// asset's keys and metrics without saving. Returns the review reasons the pixels produced.
    /// </summary>
    /// <exception cref="ImageDecodeException">The bytes do not decode as an image.</exception>
    public static async Task<IReadOnlyList<string>> CreateAsync(ImageAsset asset, byte[] original, IR2ObjectStorage storage, DerivativeOptions options, CancellationToken cancellationToken)
    {
        var processed = await Task.Run(() => ImageProcessor.Process(original, options), cancellationToken);
        var displayKey = ObjectKeyPolicy.Display(asset.Id);
        var thumbnailKey = ObjectKeyPolicy.Thumbnail(asset.Id);
        await storage.PutAsync(displayKey, processed.Display, ContentType, cancellationToken);
        await storage.PutAsync(thumbnailKey, processed.Thumbnail, ContentType, cancellationToken);
        // An approved AI copy stays what is shown; the remade derivatives are there for when it is undone.
        if (asset.DisplayCopyId is null)
        {
            asset.DisplayKey = displayKey;
            asset.ThumbnailKey = thumbnailKey;
        }
        asset.QualityMetricsJson = JsonSerializer.Serialize(processed.Metrics, Json);
        asset.DerivativesCreatedAt = DateTimeOffset.UtcNow;
        return processed.ReviewReasons;
    }

    public static async Task<byte[]?> ReadOriginalAsync(IR2ObjectStorage storage, string key, long maxBytes, CancellationToken cancellationToken)
    {
        await using var stream = await storage.OpenReadAsync(key, cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > maxBytes) return null;
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    public static Task EnqueueAsync(GlazionDbContext db, Guid imageId, CancellationToken cancellationToken) =>
        JobQueue.EnqueueAsync(db, JobTypes.ImageDerivativesCreate, EntityTypes.Image, imageId, JobQueue.DedupeKeyFor(JobTypes.ImageDerivativesCreate, imageId), null, cancellationToken);
}

/// <summary>
/// Backfill and re-generation for images that were validated before derivatives existed (or after a size change).
/// It never changes an image's approval; a not-yet-reviewed image whose pixels raise a reason moves to NeedsReview.
/// </summary>
public sealed class ImageDerivativesHandler(IR2ObjectStorage storage, IOptions<DerivativeOptions> options, ILogger<ImageDerivativesHandler> logger) : IJobHandler
{
    private readonly DerivativeOptions _options = options.Value;
    public string JobType => JobTypes.ImageDerivativesCreate;

    public async Task<JobResult> HandleAsync(ProcessingJob job, GlazionDbContext db, CancellationToken cancellationToken)
    {
        var asset = await db.ImageAssets.Include(x => x.Upload).Include(x => x.ImportRecord).SingleOrDefaultAsync(x => x.Id == job.EntityId, cancellationToken);
        if (asset is null) return JobResult.Fail("asset_not_found");
        // Not validated yet: validation creates the derivatives itself. Failed: the original is quarantined.
        if (asset.QualityStatus is QualityStatus.Pending or QualityStatus.Failed || !_options.Enabled) return JobResult.Succeeded;

        if (await storage.HeadAsync(asset.OriginalKey, cancellationToken) is null) return JobResult.Fail("missing_object", $"{asset.OriginalKey} is not in storage.");
        var original = await ImageDerivatives.ReadOriginalAsync(storage, asset.OriginalKey, _options.MaxDecodeBytes, cancellationToken);
        if (original is null) return JobResult.Fail(ImageDerivatives.SkippedTooLargeReason, $"Original is larger than {_options.MaxDecodeBytes} bytes.");

        IReadOnlyList<string> reasons;
        try { reasons = await ImageDerivatives.CreateAsync(asset, original, storage, _options, cancellationToken); }
        catch (ImageDecodeException ex) { return JobResult.Fail(ImageDerivatives.UndecodableReason, ex.Message); }

        var existing = JsonSerializer.Deserialize<List<string>>(asset.QualityReasonsJson) ?? [];
        var pixelReasons = new[] { ImageProcessor.BlurryReason, ImageProcessor.UnderexposedReason, ImageProcessor.OverexposedReason, ImageDerivatives.SkippedTooLargeReason };
        var merged = existing.Where(r => !pixelReasons.Contains(r)).Concat(reasons).Distinct().ToList();
        asset.QualityReasonsJson = JsonSerializer.Serialize(merged);
        if (asset.ApprovalStatus == ApprovalStatus.Pending && asset.QualityStatus == QualityStatus.Passed && reasons.Count > 0)
        {
            asset.QualityStatus = QualityStatus.NeedsReview;
            if (asset.ImportRecord is { Status: ImportRecordStatus.Validated } record) record.Status = ImportRecordStatus.NeedsReview;
        }
        asset.UpdatedAt = DateTimeOffset.UtcNow;
        asset.ConcurrencyStamp = Guid.NewGuid();
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Derivatives created for image {ImageId}{Reasons}", asset.Id, reasons.Count > 0 ? $" (review: {string.Join(",", reasons)})" : string.Empty);
        return JobResult.Succeeded;
    }
}
