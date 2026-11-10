using System.Text.Json;
using Glazion.Api.Data;
using Glazion.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Glazion.Api.Infrastructure;

/// <summary>
/// The single convergence point for both ingestion paths (mobile capture and bulk import). Creates the canonical
/// ImageAsset, binds provenance, links recipe/UMF chemistry, and enqueues validation. Methods add to the caller's
/// DbContext and never call SaveChanges, so the asset, its links, and its job commit in one transaction.
/// </summary>
public interface IImageIngestionService
{
    Task<ImageAsset> RegisterUploadAsync(Upload upload, StoredObject stored, CancellationToken cancellationToken);
    Task<ImageAsset> RegisterImportRecordAsync(ImportRecord record, ImportBatch batch, StoredObject stored, CancellationToken cancellationToken);
    Task AttachRecipeAsync(ImageAsset asset, Guid recipeId, Guid? actorId, bool verified, CancellationToken cancellationToken);
}

public sealed class ImageIngestionService(GlazionDbContext db) : IImageIngestionService
{
    public async Task<ImageAsset> RegisterUploadAsync(Upload upload, StoredObject stored, CancellationToken cancellationToken)
    {
        var asset = new ImageAsset
        {
            Id = Guid.CreateVersion7(),
            SourceType = ImageSourceType.UserUpload,
            UploadId = upload.Id,
            OwnerUserId = upload.OwnerUserId,
            OriginalKey = upload.ObjectKey,
            BackKey = upload.BackObjectKey,
            BackContentType = upload.BackContentType,
            DeclaredContentType = upload.ContentType,
            DeclaredSizeBytes = upload.DeclaredSizeBytes,
            DeclaredSha256 = Normalize(upload.DeclaredSha256),
            SizeBytes = stored.Size,
            ETag = stored.ETag,
            ClayBody = upload.ClayBody
        };
        db.ImageAssets.Add(asset);

        if (upload.RecipeId is { } declared)
        {
            // The user wrote this lab code on the tile; the API already checked ownership at upload creation.
            await AttachRecipeAsync(asset, declared, null, verified: false, cancellationToken);
            // The tile was fired as its recipe says (its own firing, else its base's), so the photo carries that firing.
            var recipe = await db.Recipes.AsNoTracking().Include(x => x.BaseGlaze).SingleAsync(x => x.Id == declared, cancellationToken);
            if (Firing.ConeNumber(recipe.FiringCone) is { } cone) asset.ConeMin = asset.ConeMax = cone;
            asset.Atmosphere = recipe.FiringAtmosphere;
        }
        else if (upload.CaptureCodeId is { } captureCodeId)
        {
            // No code declared: a capture code with exactly one assigned recipe identifies it unambiguously; link it
            // unverified so the curator confirms at review. Several assigned recipes leave the choice to the curator.
            var recipeIds = await db.CodeRecipes.AsNoTracking()
                .Where(x => x.CaptureCodeId == captureCodeId && x.Recipe.IsActive)
                .Select(x => x.RecipeId).Take(2).ToListAsync(cancellationToken);
            if (recipeIds.Count == 1) await AttachRecipeAsync(asset, recipeIds[0], null, verified: false, cancellationToken);
        }

        await JobQueue.EnqueueAsync(db, JobTypes.ImageValidate, EntityTypes.Image, asset.Id, JobQueue.DedupeKeyFor(JobTypes.ImageValidate, asset.Id), null, cancellationToken);
        return asset;
    }

    public async Task<ImageAsset> RegisterImportRecordAsync(ImportRecord record, ImportBatch batch, StoredObject stored, CancellationToken cancellationToken)
    {
        var asset = new ImageAsset
        {
            Id = Guid.CreateVersion7(),
            SourceType = ImageSourceType.Import,
            ImportRecordId = record.Id,
            OwnerUserId = batch.CreatedBy,
            TileVariantKey = record.TileVariantKey,
            OriginalKey = record.ObjectKey,
            DeclaredContentType = record.DeclaredContentType,
            DeclaredSizeBytes = record.DeclaredSizeBytes,
            DeclaredSha256 = Normalize(record.DeclaredSha256),
            SizeBytes = stored.Size,
            ETag = stored.ETag,
            ConeMin = record.ConeMin,
            ConeMax = record.ConeMax,
            Atmosphere = record.Atmosphere,
            ClayBody = record.ClayBody,
            LabelsJson = record.LabelsJson
        };
        db.ImageAssets.Add(asset);
        record.ImageAsset = asset;

        if (record.RecipeId is { } recipeId)
        {
            await AttachRecipeAsync(asset, recipeId, batch.CreatedBy, verified: false, cancellationToken);
        }
        else if (!string.IsNullOrWhiteSpace(record.UmfJson))
        {
            using var document = JsonDocument.Parse(record.UmfJson);
            var validation = ChemistrySchema.ValidateUmf(document.RootElement);
            if (!validation.IsValid)
            {
                asset.ChemistryStatus = ChemistryStatus.Invalid;
                record.ChemistryWarningsJson = JsonSerializer.Serialize(validation.Errors);
            }
            else
            {
                var recipe = new Recipe
                {
                    Id = Guid.CreateVersion7(),
                    Name = $"{batch.Name}: {record.ExternalRecordId}",
                    Description = $"Imported with batch {batch.Id:D} from source '{batch.SourceLabel}'.",
                    SourceType = RecipeSourceType.Imported,
                    SourceReference = $"{batch.Id:D}:{record.ExternalRecordId}",
                    RecipeJson = JsonSerializer.Serialize(new
                    {
                        umf = JsonSerializer.Deserialize<JsonElement>(validation.CanonicalJson),
                        firing = new { coneMin = record.ConeMin, coneMax = record.ConeMax, atmosphere = record.Atmosphere },
                        tileVariant = record.TileVariantKey
                    })
                };
                var snapshot = new RecipeCalculationResult
                {
                    Id = Guid.CreateVersion7(),
                    RecipeId = recipe.Id,
                    CalculationProfile = ChemistrySchema.ProvidedUmfProfile,
                    CalculationVersion = ChemistrySchema.ProvidedUmfCalculationVersion,
                    MaterialAnalysisVersion = ChemistrySchema.NoMaterialAnalysis,
                    ChemistrySchemaVersion = ChemistrySchema.Version,
                    InputHash = validation.InputHash,
                    UmfJson = validation.CanonicalJson,
                    FluxTotal = validation.FluxTotal,
                    WarningsJson = JsonSerializer.Serialize(validation.Warnings)
                };
                db.Recipes.Add(recipe);
                db.RecipeCalculationResults.Add(snapshot);
                db.RecipeImages.Add(new RecipeImage { RecipeId = recipe.Id, ImageId = asset.Id, RelationshipType = RecipeImageRelationship.TestTile, Verified = false, CreatedBy = batch.CreatedBy });
                asset.CalculationResultId = snapshot.Id;
                asset.ChemistryStatus = ChemistryStatus.Linked;
                record.ChemistryWarningsJson = JsonSerializer.Serialize(validation.Warnings);
            }
        }

        await JobQueue.EnqueueAsync(db, JobTypes.ImageValidate, EntityTypes.Image, asset.Id, JobQueue.DedupeKeyFor(JobTypes.ImageValidate, asset.Id), null, cancellationToken);
        return asset;
    }

    public async Task AttachRecipeAsync(ImageAsset asset, Guid recipeId, Guid? actorId, bool verified, CancellationToken cancellationToken)
    {
        var recipeExists = await db.Recipes.AnyAsync(x => x.Id == recipeId && x.IsActive, cancellationToken);
        if (!recipeExists) throw new InvalidOperationException($"Recipe {recipeId} does not exist or is inactive.");

        var link = db.RecipeImages.Local.FirstOrDefault(x => x.RecipeId == recipeId && x.ImageId == asset.Id)
                   ?? await db.RecipeImages.SingleOrDefaultAsync(x => x.RecipeId == recipeId && x.ImageId == asset.Id, cancellationToken);
        if (link is null)
        {
            db.RecipeImages.Add(new RecipeImage { RecipeId = recipeId, ImageId = asset.Id, RelationshipType = RecipeImageRelationship.TestTile, Verified = verified, CreatedBy = actorId });
        }
        else if (verified && !link.Verified) link.Verified = true;

        // Pin the newest snapshot so the image records exactly which chemistry it will be indexed against.
        var snapshotId = await db.RecipeCalculationResults.AsNoTracking()
            .Where(x => x.RecipeId == recipeId).OrderByDescending(x => x.CreatedAt).Select(x => (Guid?)x.Id).FirstOrDefaultAsync(cancellationToken);
        asset.CalculationResultId = snapshotId;
        asset.ChemistryStatus = snapshotId is null ? ChemistryStatus.Pending : ChemistryStatus.Linked;
        asset.SourceVersion++;
        asset.UpdatedAt = DateTimeOffset.UtcNow;
    }

    private static string? Normalize(string? sha256) => string.IsNullOrWhiteSpace(sha256) ? null : sha256.Trim().ToLowerInvariant();
}

/// <summary>
/// image.validate: proves the stored bytes are the declared image. Streams the object once (SHA-256 over all
/// bytes, header parse on the first buffer), applies structural policy, quarantines hard failures, and routes
/// soft findings to review. Idempotent: re-running on the same object rewrites the same facts.
/// </summary>
public sealed class ImageValidateHandler(IR2ObjectStorage storage, Microsoft.Extensions.Options.IOptions<IngestionOptions> options,
    Microsoft.Extensions.Options.IOptions<DerivativeOptions> derivativeOptions, ILogger<ImageValidateHandler> logger) : IJobHandler
{
    private readonly IngestionOptions _options = options.Value;
    private readonly DerivativeOptions _derivatives = derivativeOptions.Value;
    public string JobType => JobTypes.ImageValidate;

    public async Task<JobResult> HandleAsync(ProcessingJob job, GlazionDbContext db, CancellationToken cancellationToken)
    {
        var asset = await db.ImageAssets.Include(x => x.Upload).Include(x => x.ImportRecord).ThenInclude(x => x!.Batch)
            .SingleOrDefaultAsync(x => x.Id == job.EntityId, cancellationToken);
        if (asset is null) return JobResult.Fail("asset_not_found");
        if (asset.QualityStatus == QualityStatus.Failed) return JobResult.Succeeded;
        if (asset.ApprovalStatus is ApprovalStatus.Approved or ApprovalStatus.Rejected) return JobResult.Succeeded;

        if (asset.Upload is { } u && u.Status == UploadStatus.Uploaded) { u.Status = UploadStatus.Validating; u.ConcurrencyStamp = Guid.NewGuid(); await db.SaveChangesAsync(cancellationToken); }

        var stored = await storage.HeadAsync(asset.OriginalKey, cancellationToken);
        var hardFailures = new List<string>();
        var reviewReasons = new List<string>();
        if (stored is null)
        {
            hardFailures.Add("missing_object");
            await MarkFailedAsync(db, asset, hardFailures, quarantine: false, cancellationToken);
            return JobResult.Succeeded;
        }

        string sha256;
        long byteCount;
        ImageHeader? header;
        byte[]? content;
        await using (var stream = await storage.OpenReadAsync(asset.OriginalKey, cancellationToken))
        {
            // One pass: hash every byte, keep the header, and keep the whole object when it is small enough to decode.
            var keepAll = _derivatives.Enabled && stored.Size <= _derivatives.MaxDecodeBytes;
            var (hash, count, head, all) = await HashAndBufferAsync(stream, _options.HeaderBufferBytes, keepAll ? _derivatives.MaxDecodeBytes : 0, cancellationToken);
            sha256 = hash; byteCount = count; header = ImageHeaderReader.TryRead(head); content = all;
        }

        if (byteCount != asset.DeclaredSizeBytes) hardFailures.Add("size_mismatch");
        if (asset.DeclaredSha256 is not null && !string.Equals(asset.DeclaredSha256, sha256, StringComparison.OrdinalIgnoreCase)) hardFailures.Add("checksum_mismatch");
        if (header is null) hardFailures.Add("undecodable_header");
        else
        {
            if (!string.Equals(header.ContentType, asset.DeclaredContentType, StringComparison.OrdinalIgnoreCase)) hardFailures.Add("content_type_mismatch");
            if ((long)header.Width * header.Height > _options.MaxPixels) hardFailures.Add("dimensions_exceed_policy");
            else if (header.Width < _options.MinWidth || header.Height < _options.MinHeight) reviewReasons.Add("below_minimum_dimensions");
        }

        // Pixel checks and derivatives. Only bytes that passed the structural checks above reach the decoder.
        if (hardFailures.Count == 0 && _derivatives.Enabled)
        {
            if (content is null) reviewReasons.Add(ImageDerivatives.SkippedTooLargeReason);
            else
            {
                try { reviewReasons.AddRange(await ImageDerivatives.CreateAsync(asset, content, storage, _derivatives, cancellationToken)); }
                catch (ImageDecodeException ex)
                {
                    logger.LogWarning("Image {ImageId} has a valid header but does not decode: {Message}", asset.Id, ex.Message);
                    hardFailures.Add(ImageDerivatives.UndecodableReason);
                }
            }
        }

        if (hardFailures.Count > 0)
        {
            await MarkFailedAsync(db, asset, hardFailures, quarantine: true, cancellationToken);
            return JobResult.Succeeded;
        }

        // Duplicate detection must be serialised per content hash: two identical files validated concurrently would
        // otherwise each look before the other has committed its Sha256. A transaction-scoped advisory lock keyed on
        // the hash makes the check-then-write atomic across workers without locking unrelated rows.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtext({sha256}))", cancellationToken);
        var duplicateOf = await db.ImageAssets.AsNoTracking()
            .Where(x => x.Sha256 == sha256 && x.Id != asset.Id && x.QualityStatus != QualityStatus.Failed)
            .OrderBy(x => x.CreatedAt).Select(x => (Guid?)x.Id).FirstOrDefaultAsync(cancellationToken);
        if (duplicateOf is not null) { reviewReasons.Add("duplicate_content"); asset.DuplicateOfImageId = duplicateOf; }

        asset.Format = header!.Format;
        asset.Width = header.Width;
        asset.Height = header.Height;
        asset.Sha256 = sha256;
        asset.SizeBytes = byteCount;
        asset.ETag = stored.ETag;
        asset.QualityPolicyVersion = _options.QualityPolicyVersion;
        asset.QualityReasonsJson = JsonSerializer.Serialize(reviewReasons);
        asset.QualityStatus = reviewReasons.Count == 0 ? QualityStatus.Passed : QualityStatus.NeedsReview;
        asset.ValidatedAt = DateTimeOffset.UtcNow;
        asset.UpdatedAt = asset.ValidatedAt.Value;
        asset.ConcurrencyStamp = Guid.NewGuid();

        if (asset.Upload is { } upload)
        {
            upload.Status = UploadStatus.NeedsReview;
            upload.ActualSizeBytes = byteCount;
            upload.ConcurrencyStamp = Guid.NewGuid();
        }
        if (asset.ImportRecord is { } record)
        {
            record.Status = asset.QualityStatus == QualityStatus.Passed ? ImportRecordStatus.Validated : ImportRecordStatus.NeedsReview;
            if (record.Batch.AutoApprove && asset.QualityStatus == QualityStatus.Passed)
            {
                asset.ApprovalStatus = ApprovalStatus.Approved;
                asset.ApprovedAt = DateTimeOffset.UtcNow;
                asset.ApprovedBy = record.Batch.CreatedBy;
                asset.SourceVersion++;
                await Glazion.Api.Indexing.IndexJobs.EnqueueUpsertAsync(db, asset.Id, cancellationToken);
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return JobResult.Succeeded;
    }

    private async Task MarkFailedAsync(GlazionDbContext db, ImageAsset asset, List<string> reasons, bool quarantine, CancellationToken cancellationToken)
    {
        if (quarantine)
        {
            var quarantineKey = ObjectKeyPolicy.Quarantine(asset.OriginalKey);
            await storage.CopyAsync(asset.OriginalKey, quarantineKey, cancellationToken);
            await storage.DeleteAsync(asset.OriginalKey, cancellationToken);
            logger.LogWarning("Image {ImageId} quarantined: {Reasons}", asset.Id, string.Join(",", reasons));
        }
        var reason = string.Join(",", reasons);
        asset.QualityStatus = QualityStatus.Failed;
        asset.QualityPolicyVersion = _options.QualityPolicyVersion;
        asset.QualityReasonsJson = JsonSerializer.Serialize(reasons);
        asset.ValidatedAt = DateTimeOffset.UtcNow;
        asset.UpdatedAt = asset.ValidatedAt.Value;
        asset.ConcurrencyStamp = Guid.NewGuid();
        if (asset.Upload is { } upload) { upload.Status = UploadStatus.Failed; upload.FailureReason = reason; upload.ConcurrencyStamp = Guid.NewGuid(); }
        if (asset.ImportRecord is { } record) { record.Status = ImportRecordStatus.Failed; record.FailureCode = reasons[0]; record.FailureDetail = reason; }
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Hashes the whole stream, keeps the first <paramref name="headSize"/> bytes, and keeps everything when it fits in <paramref name="keepAllUpTo"/> (0 = never).</summary>
    private static async Task<(string Sha256, long Count, byte[] Head, byte[]? All)> HashAndBufferAsync(Stream stream, int headSize, long keepAllUpTo, CancellationToken cancellationToken)
    {
        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        var head = new byte[headSize];
        var headFilled = 0;
        var buffer = new byte[81920];
        long count = 0;
        int read;
        var all = keepAllUpTo > 0 ? new MemoryStream() : null;
        while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            hash.AppendData(buffer, 0, read);
            count += read;
            if (all is not null)
            {
                if (count <= keepAllUpTo) all.Write(buffer, 0, read);
                else { all.Dispose(); all = null; }
            }
            if (headFilled < headSize)
            {
                var take = Math.Min(read, headSize - headFilled);
                Buffer.BlockCopy(buffer, 0, head, headFilled, take);
                headFilled += take;
            }
        }
        return (Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(), count, headFilled == headSize ? head : head[..headFilled], all?.ToArray());
    }
}
