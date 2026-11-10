using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Glazion.Api.Contracts;
using Glazion.Api.Data;
using Glazion.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Glazion.Api.Infrastructure;

// AI copies (docs/features/ai-copies.md): the imported corpus's photos are not ours to show, so each can be remade by an image
// model from the photo itself, the same glaze photographed afresh. An admin compares the copy with the original and
// approves it; the approved copy is then what every app shows. Copies are made once and kept, never per request.

/// <summary>Configuration section "AiCopies".</summary>
public sealed class AiCopyOptions
{
    /// <summary>"OpenAI" or "Disabled".</summary>
    public string Provider { get; set; } = "OpenAI";
    public string BaseUrl { get; set; } = "https://api.openai.com/v1";
    /// <summary>Left empty, the chat's key (Chat:OpenAI:ApiKey) is used.</summary>
    public string ApiKey { get; set; } = string.Empty;
    /// <summary>The image editing model. gpt-image-1 was shut down on 23 Oct 2026; sunburst is OpenAI's most precise editor.</summary>
    public string Model { get; set; } = "gpt-image-2.5-sunburst";
    /// <summary>low, medium, high, xhigh or max: more is closer to the photo and costs more.</summary>
    public string Quality { get; set; } = "high";
    public string Size { get; set; } = "1024x1024";
    public int OutputCompression { get; set; } = 90;
    public int TimeoutSeconds { get; set; } = 240;
    /// <summary>Copies one image may have waiting or being made at once, so a double click costs one copy.</summary>
    public int MaxInFlightPerImage { get; set; } = 1;

    public bool Enabled => string.Equals(Provider, "OpenAI", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(ApiKey);

    public static readonly string[] Qualities = ["low", "medium", "high", "xhigh", "max"];
}

public sealed record GeneratedImage(byte[] Bytes, string? UsageJson);

/// <summary>Code is machine-readable; Transient means trying again later may work (rate limit, outage).</summary>
public sealed class AiImageException(string code, string message, bool transient) : Exception(message)
{
    public string Code { get; } = code;
    public bool Transient { get; } = transient;
}

public interface IAiImageModel
{
    string Model { get; }
    Task<GeneratedImage> EditAsync(byte[] source, string sourceContentType, string prompt, string quality, CancellationToken cancellationToken);
}

/// <summary>OpenAI's image edit endpoint (POST /v1/images/edits). The key is never logged.</summary>
public sealed class OpenAiImageModel : IAiImageModel
{
    private readonly HttpClient _http;
    private readonly AiCopyOptions _options;
    private readonly ILogger<OpenAiImageModel> _logger;

    public OpenAiImageModel(HttpClient http, IOptions<AiCopyOptions> options, ILogger<OpenAiImageModel> logger)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;
        _http.BaseAddress = new Uri(_options.BaseUrl.TrimEnd('/') + "/");
        if (!string.IsNullOrWhiteSpace(_options.ApiKey)) _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        _http.Timeout = TimeSpan.FromSeconds(Math.Max(30, _options.TimeoutSeconds));
    }

    public string Model => _options.Model;

    public async Task<GeneratedImage> EditAsync(byte[] source, string sourceContentType, string prompt, string quality, CancellationToken cancellationToken)
    {
        if (!_options.Enabled) throw new AiImageException("ai_copies_not_configured", "AI copies are not set up: no image model key.", transient: false);
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(_options.Model), "model");
        form.Add(new StringContent(prompt), "prompt");
        var image = new ByteArrayContent(source);
        image.Headers.ContentType = new MediaTypeHeaderValue(sourceContentType);
        form.Add(image, "image", "tile." + ObjectKeyPolicy.ExtensionFor(sourceContentType));
        form.Add(new StringContent(_options.Size), "size");
        form.Add(new StringContent(quality), "quality");
        form.Add(new StringContent("webp"), "output_format");
        form.Add(new StringContent(_options.OutputCompression.ToString(CultureInfo.InvariantCulture)), "output_compression");
        form.Add(new StringContent("1"), "n");

        HttpResponseMessage response;
        try { response = await _http.PostAsync("images/edits", form, cancellationToken); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new AiImageException("image_model_unavailable", "The image model could not be reached.", transient: true);
        }
        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var message = ErrorMessage(text);
                _logger.LogWarning("Image model returned {Status}: {Message}", (int)response.StatusCode, message);
                var transient = response.StatusCode is HttpStatusCode.TooManyRequests or >= HttpStatusCode.InternalServerError;
                var code = response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "image_model_refused_key",
                    HttpStatusCode.TooManyRequests => "image_model_busy",
                    HttpStatusCode.BadRequest => "image_model_rejected",
                    _ => "image_model_unavailable"
                };
                throw new AiImageException(code, $"The image model returned {(int)response.StatusCode}: {message}", transient);
            }
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array || data.GetArrayLength() == 0
                || !data[0].TryGetProperty("b64_json", out var b64) || b64.ValueKind != JsonValueKind.String)
                throw new AiImageException("image_model_empty", "The image model returned no image.", transient: true);
            var usage = root.TryGetProperty("usage", out var u) ? u.GetRawText() : null;
            return new GeneratedImage(Convert.FromBase64String(b64.GetString()!), usage);
        }
    }

    private static string ErrorMessage(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var e) && e.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String)
                return Truncate(m.GetString()!, 300);
        }
        catch (JsonException) { }
        return Truncate(body, 300);
    }

    private static string Truncate(string text, int max) => text.Length > max ? text[..max] + "…" : text;
}

/// <summary>
/// What the image model is asked. The photo is the authority: the copy keeps its glaze exactly (colour, gloss,
/// texture; the meeting's "same colour, same glossiness", nothing invented) and changes only the photography, so it
/// is a new picture rather than the same one. The recipe is context, never a reason to change what the photo shows.
/// </summary>
public static class AiCopyPrompt
{
    public static string Build(ImageAsset asset, Recipe? recipe, string? instruction)
    {
        var text = new StringBuilder();
        text.AppendLine("The image is a photograph of a fired ceramic glaze test tile. Make a new photograph of a test tile glazed with exactly the same glaze, to be published in place of this photo.");
        text.AppendLine();
        text.AppendLine("Keep the glaze exactly as it is in the photo: the same colour and hue, how light or dark it is, its saturation, whether it is glossy, satin or matte, how opaque it is, and only the variation the photo shows (speckles, breaking over edges, pooling, crawling, crystals). Do not add texture, patterns, crackle, drips or effects the photo does not show. Do not change the colours. Do not make it look smoother, shinier or more perfect than it is.");
        text.AppendLine();
        text.AppendLine("Change only the photography, so the new photo is not a copy of the original: the same kind of tile in a slightly different position and framing, on a plain neutral background, in soft even daylight. It must look like a real photograph, not a rendering. No text, labels, hands or props.");
        var context = new List<string>();
        if (recipe is not null)
        {
            var additions = Rows(recipe.AdditionsJson);
            if (additions.Count > 0) context.Add($"Its colorants: {string.Join(", ", additions)}.");
        }
        var firing = new List<string>();
        if (asset.ConeMin is { } cone) firing.Add($"cone {Cone(cone)}");
        if (!string.IsNullOrWhiteSpace(asset.Atmosphere)) firing.Add(asset.Atmosphere.ToLowerInvariant());
        if (!string.IsNullOrWhiteSpace(asset.ClayBody)) firing.Add($"on {asset.ClayBody.Replace("red_body", "red clay")}");
        if (firing.Count > 0) context.Add($"Fired {string.Join(", ", firing)}.");
        if (context.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("For reference only (the photo decides how it looks): " + string.Join(" ", context));
        }
        if (!string.IsNullOrWhiteSpace(instruction))
        {
            text.AppendLine();
            text.AppendLine("Also: " + instruction.Trim());
        }
        return text.ToString().TrimEnd();
    }

    private static string Cone(decimal cone) => cone < 0 ? "0" + (-cone).ToString("0.#", CultureInfo.InvariantCulture) : cone.ToString("0.#", CultureInfo.InvariantCulture);

    private static List<string> Rows(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "[]" : json);
            return doc.RootElement.ValueKind != JsonValueKind.Array ? [] : doc.RootElement.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.Object && e.TryGetProperty("rawName", out var n) && n.ValueKind == JsonValueKind.String)
                .Select(e => $"{(e.TryGetProperty("amount", out var a) && a.ValueKind == JsonValueKind.Number ? a.GetDecimal().ToString("0.###", CultureInfo.InvariantCulture) + "% " : string.Empty)}{e.GetProperty("rawName").GetString()}")
                .ToList();
        }
        catch (JsonException) { return []; }
    }
}

public sealed record AiCopyDto(Guid Id, Guid ImageId, AiCopyStatus Status, string Model, string Quality, string? Instruction, Uri? DisplayUrl, Uri? ThumbnailUrl,
    int? Width, int? Height, string? Error, AiCopyUsage? Usage, Guid CreatedBy, DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt, DateTimeOffset? ReviewedAt);

/// <summary>Tokens the model reported for the copy: the input photo and prompt, and the image it made.</summary>
public sealed record AiCopyUsage(int? InputTokens, int? OutputTokens, int? TotalTokens);

public sealed record MakeAiCopyRequest(string? Instruction, string? Quality);

/// <summary>Make, list, approve, reject and undo AI copies of one image. The making itself is the <see cref="AiCopyHandler"/> job.</summary>
public sealed class AiCopyService(GlazionDbContext db, IR2ObjectStorage storage, IOptions<AiCopyOptions> options)
{
    public const string JobType = "image.ai-copy";
    private readonly AiCopyOptions _options = options.Value;

    public async Task<IReadOnlyList<AiCopyDto>> ListAsync(Guid imageId, CancellationToken cancellationToken)
    {
        var copies = await db.AiImageCopies.AsNoTracking().Where(x => x.ImageId == imageId).OrderByDescending(x => x.CreatedAt).ToListAsync(cancellationToken);
        var result = new List<AiCopyDto>(copies.Count);
        foreach (var copy in copies) result.Add(await ToDtoAsync(copy, cancellationToken));
        return result;
    }

    public async Task<AiCopyDto> MakeAsync(Guid imageId, MakeAiCopyRequest request, Guid adminId, CancellationToken cancellationToken)
    {
        if (!_options.Enabled) throw new ApiProblemException(503, "ai_copies_not_configured", "AI copies are not set up", "No image model key is configured (AiCopies:ApiKey, or the chat's Chat:OpenAI:ApiKey).");
        var instruction = string.IsNullOrWhiteSpace(request.Instruction) ? null : request.Instruction.Trim();
        if (instruction is { Length: > 500 }) throw ApiProblems.Validation("instruction", "Keep the instruction to 500 characters.");
        var quality = string.IsNullOrWhiteSpace(request.Quality) ? _options.Quality : request.Quality.Trim().ToLowerInvariant();
        if (!AiCopyOptions.Qualities.Contains(quality)) throw ApiProblems.Validation("quality", $"Quality must be one of {string.Join(", ", AiCopyOptions.Qualities)}.");

        var asset = await db.ImageAssets.AsNoTracking().Include(x => x.Recipes).ThenInclude(x => x.Recipe).SingleOrDefaultAsync(x => x.Id == imageId, cancellationToken)
                    ?? throw ApiProblems.NotFound("Image");
        if (asset.QualityStatus is QualityStatus.Pending or QualityStatus.Failed)
            throw new ApiProblemException(409, "image_not_ready", "Image not ready", "Only a photo that passed its checks can be copied.");
        var inFlight = await db.AiImageCopies.CountAsync(x => x.ImageId == imageId && (x.Status == AiCopyStatus.Queued || x.Status == AiCopyStatus.Generating), cancellationToken);
        if (inFlight >= _options.MaxInFlightPerImage)
            throw new ApiProblemException(409, "ai_copy_in_progress", "A copy is already being made", "Wait for the copy being made to finish, then make another.");

        var recipe = asset.Recipes.OrderByDescending(x => x.Verified).ThenBy(x => x.CreatedAt).Select(x => x.Recipe).FirstOrDefault(x => x is not null);
        var copy = new AiImageCopy
        {
            Id = Guid.CreateVersion7(), ImageId = imageId, Status = AiCopyStatus.Queued, Model = _options.Model, Quality = quality,
            Prompt = AiCopyPrompt.Build(asset, recipe, instruction), Instruction = instruction, CreatedBy = adminId, CreatedAt = DateTimeOffset.UtcNow
        };
        db.AiImageCopies.Add(copy);
        await JobQueue.EnqueueAsync(db, JobType, "ai_copy", copy.Id, JobQueue.DedupeKeyFor(JobType, copy.Id), null, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return await ToDtoAsync(copy, cancellationToken);
    }

    /// <summary>Shows the copy in place of the photo everywhere (search results, chat, the apps). One approved copy per image.</summary>
    public async Task<AiCopyDto> ApproveAsync(Guid imageId, Guid copyId, Guid adminId, CancellationToken cancellationToken)
    {
        var copy = await FindAsync(imageId, copyId, cancellationToken);
        if (copy.Status is not (AiCopyStatus.Ready or AiCopyStatus.Rejected or AiCopyStatus.Approved) || copy.Key is null || copy.ThumbnailKey is null)
            throw new ApiProblemException(409, "ai_copy_not_ready", "Copy not ready", "Only a finished copy can be approved.");
        var asset = await db.ImageAssets.SingleAsync(x => x.Id == imageId, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        if (asset.DisplayCopyId is { } previous && previous != copy.Id)
            await db.AiImageCopies.Where(x => x.Id == previous && x.Status == AiCopyStatus.Approved)
                .ForEachAsync(x => x.Status = AiCopyStatus.Ready, cancellationToken);
        copy.Status = AiCopyStatus.Approved;
        copy.ReviewedBy = adminId;
        copy.ReviewedAt = now;
        asset.DisplayKey = copy.Key;
        asset.ThumbnailKey = copy.ThumbnailKey;
        asset.DisplayCopyId = copy.Id;
        Touch(asset, now);
        await db.SaveChangesAsync(cancellationToken);
        return await ToDtoAsync(copy, cancellationToken);
    }

    public async Task<AiCopyDto> RejectAsync(Guid imageId, Guid copyId, Guid adminId, CancellationToken cancellationToken)
    {
        var copy = await FindAsync(imageId, copyId, cancellationToken);
        if (copy.Status is AiCopyStatus.Queued or AiCopyStatus.Generating)
            throw new ApiProblemException(409, "ai_copy_in_progress", "Copy still being made", "Wait for it to finish, then discard it.");
        var now = DateTimeOffset.UtcNow;
        if (copy.Status == AiCopyStatus.Approved)
        {
            var asset = await db.ImageAssets.SingleAsync(x => x.Id == imageId, cancellationToken);
            ShowOriginal(asset, now);
        }
        copy.Status = AiCopyStatus.Rejected;
        copy.ReviewedBy = adminId;
        copy.ReviewedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        return await ToDtoAsync(copy, cancellationToken);
    }

    /// <summary>Back to showing the photo itself; the approved copy stays, as Ready, and can be approved again.</summary>
    public async Task RevertAsync(Guid imageId, CancellationToken cancellationToken)
    {
        var asset = await db.ImageAssets.SingleOrDefaultAsync(x => x.Id == imageId, cancellationToken) ?? throw ApiProblems.NotFound("Image");
        if (asset.DisplayCopyId is not { } shown) return;
        await db.AiImageCopies.Where(x => x.Id == shown && x.Status == AiCopyStatus.Approved).ForEachAsync(x => x.Status = AiCopyStatus.Ready, cancellationToken);
        ShowOriginal(asset, DateTimeOffset.UtcNow);
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>The photo's own derivatives (made from the original, at fixed keys), or none when it never had them.</summary>
    private static void ShowOriginal(ImageAsset asset, DateTimeOffset now)
    {
        asset.DisplayKey = asset.DerivativesCreatedAt is null ? null : ObjectKeyPolicy.Display(asset.Id);
        asset.ThumbnailKey = asset.DerivativesCreatedAt is null ? null : ObjectKeyPolicy.Thumbnail(asset.Id);
        asset.DisplayCopyId = null;
        Touch(asset, now);
    }

    private static void Touch(ImageAsset asset, DateTimeOffset now)
    {
        asset.UpdatedAt = now;
        asset.ConcurrencyStamp = Guid.NewGuid();
    }

    private async Task<AiImageCopy> FindAsync(Guid imageId, Guid copyId, CancellationToken cancellationToken) =>
        await db.AiImageCopies.SingleOrDefaultAsync(x => x.Id == copyId && x.ImageId == imageId, cancellationToken)
        ?? throw new ApiProblemException(404, "ai_copy_not_found", "Copy not found", "No AI copy with this ID for this image.");

    private async Task<AiCopyDto> ToDtoAsync(AiImageCopy copy, CancellationToken cancellationToken)
    {
        Uri? display = copy.Key is null ? null : await storage.CreateGetUrlAsync(copy.Key, $"ai-copy-{copy.Id:N}.webp", cancellationToken);
        Uri? thumbnail = copy.ThumbnailKey is null ? null : await storage.CreateGetUrlAsync(copy.ThumbnailKey, cancellationToken);
        return new AiCopyDto(copy.Id, copy.ImageId, copy.Status, copy.Model, copy.Quality, copy.Instruction, display, thumbnail, copy.Width, copy.Height, copy.Error,
            Usage(copy.UsageJson), copy.CreatedBy, copy.CreatedAt, copy.CompletedAt, copy.ReviewedAt);
    }

    private static AiCopyUsage? Usage(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            int? Int(string name) => doc.RootElement.TryGetProperty(name, out var v) && v.TryGetInt32(out var n) ? n : null;
            return new AiCopyUsage(Int("input_tokens"), Int("output_tokens"), Int("total_tokens"));
        }
        catch (JsonException) { return null; }
    }
}

/// <summary>
/// Makes one copy: reads the photo (its upright display derivative when it has one), asks the image model, and stores
/// the result at display and thumbnail size. Retries a rate limit or outage; anything else fails the copy with the
/// model's reason, for the admin to read.
/// </summary>
public sealed class AiCopyHandler(IR2ObjectStorage storage, IAiImageModel model, IOptions<DerivativeOptions> derivativeOptions, ILogger<AiCopyHandler> logger) : IJobHandler
{
    private const long MaxSourceBytes = 45L * 1024 * 1024;
    public string JobType => AiCopyService.JobType;

    public async Task<JobResult> HandleAsync(ProcessingJob job, GlazionDbContext db, CancellationToken cancellationToken)
    {
        var copy = await db.AiImageCopies.Include(x => x.Image).SingleOrDefaultAsync(x => x.Id == job.EntityId, cancellationToken);
        if (copy is null) return JobResult.Fail("ai_copy_not_found");
        if (copy.Status is not (AiCopyStatus.Queued or AiCopyStatus.Generating)) return JobResult.Succeeded;
        copy.Status = AiCopyStatus.Generating;
        await db.SaveChangesAsync(cancellationToken);

        try
        {
            var asset = copy.Image;
            // The upright display derivative of the original (never an earlier copy), else the original itself.
            var (key, contentType) = asset.DerivativesCreatedAt is not null ? (ObjectKeyPolicy.Display(asset.Id), ImageDerivatives.ContentType) : (asset.OriginalKey, asset.DeclaredContentType);
            var source = await ImageDerivatives.ReadOriginalAsync(storage, key, MaxSourceBytes, cancellationToken)
                         ?? throw new AiImageException("source_too_large", "The photo is too large to send to the image model.", transient: false);
            var generated = await model.EditAsync(source, contentType, copy.Prompt, copy.Quality, cancellationToken);

            ProcessedImage processed;
            try { processed = ImageProcessor.Process(generated.Bytes, derivativeOptions.Value); }
            catch (ImageDecodeException) { throw new AiImageException("image_model_unreadable", "The image model's picture could not be read.", transient: true); }
            copy.Key = ObjectKeyPolicy.AiCopy(asset.Id, copy.Id);
            copy.ThumbnailKey = ObjectKeyPolicy.AiCopyThumbnail(asset.Id, copy.Id);
            await storage.PutAsync(copy.Key, processed.Display, ImageDerivatives.ContentType, cancellationToken);
            await storage.PutAsync(copy.ThumbnailKey, processed.Thumbnail, ImageDerivatives.ContentType, cancellationToken);
            copy.Width = processed.Metrics.Width;
            copy.Height = processed.Metrics.Height;
            copy.UsageJson = generated.UsageJson;
            copy.Status = AiCopyStatus.Ready;
            copy.Error = null;
            copy.CompletedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation("AI copy {CopyId} of image {ImageId} made with {Model}", copy.Id, asset.Id, copy.Model);
            return JobResult.Succeeded;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // Anything but the model's own refusal is worth another try; the last try leaves the copy Failed, not stuck.
            var (code, transient) = ex is AiImageException ai ? (ai.Code, ai.Transient) : (ex.GetType().Name, true);
            if (ex is not AiImageException) logger.LogWarning(ex, "AI copy {CopyId} attempt {Attempt} failed", copy.Id, job.Attempts);
            if (transient && job.Attempts < job.MaxAttempts)
            {
                copy.Status = AiCopyStatus.Queued;
                await db.SaveChangesAsync(cancellationToken);
                return JobResult.Retry(code, ex.Message);
            }
            copy.Status = AiCopyStatus.Failed;
            copy.Error = ex.Message.Length > 1000 ? ex.Message[..1000] : ex.Message;
            copy.CompletedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            return JobResult.Fail(code, ex.Message);
        }
    }
}
