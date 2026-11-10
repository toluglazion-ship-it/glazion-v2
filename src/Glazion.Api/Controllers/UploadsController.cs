using Glazion.Api.Contracts;
using Glazion.Api.Data;
using Glazion.Api.Domain;
using Glazion.Api.Infrastructure;
using Glazion.Api.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Glazion.Api.Controllers;

/// <summary>
/// The capture flow: create → presign → PUT to R2 → complete. The API never receives the image bytes; it signs one
/// exact key, then verifies the stored object before anything else happens (docs/architecture/recipe-to-image/image-ingestion-pipeline.md).
/// </summary>
[ApiController]
[Route("api/v2/uploads")]
[Authorize]
public sealed class UploadsController(GlazionDbContext db, IR2ObjectStorage storage, IImageIngestionService ingestion, LabCodeService labCodes, TileTaskService tasks, IOptions<UploadOptions> uploadOptions, IOptions<IngestionOptions> ingestionOptions, ILogger<UploadsController> logger) : ControllerBase
{
    [HttpGet]
    public Task<PagedResult<UploadDto>> List([FromQuery] string? cursor, [FromQuery] int? pageSize, [FromQuery] UploadStatus? status, CancellationToken cancellationToken)
    {
        var query = db.Uploads.AsNoTracking().Include(x => x.ImageAsset).Include(x => x.Recipe).Where(x => x.OwnerUserId == User.GetUserId());
        if (status is not null) query = query.Where(x => x.Status == status);
        return Paging.PageAsync(query.OrderByDescending(x => x.CreatedAt).ThenBy(x => x.Id), cursor, pageSize, async rows =>
        {
            var result = new List<UploadDto>(rows.Count);
            foreach (var upload in rows) result.Add(await BuildAsync(upload, cancellationToken));
            return (IReadOnlyList<UploadDto>)result;
        }, cancellationToken);
    }

    [HttpPost]
    public async Task<ActionResult<UploadDto>> Create(CreateUploadRequest request, CancellationToken cancellationToken)
    {
        var options = uploadOptions.Value;
        var contentType = UploadContentTypes.Resolve(request.ContentType, request.FileName);
        if (contentType is null || !options.AllowedContentTypes.Contains(contentType, StringComparer.OrdinalIgnoreCase))
            throw new ApiProblemException(400, "unsupported_content_type", "Unsupported photo type", $"Send a JPEG, PNG or WebP photo{(string.IsNullOrWhiteSpace(request.ContentType) ? "" : $" (got {request.ContentType})")}.");
        var fileName = string.IsNullOrWhiteSpace(request.FileName) ? $"photo{UploadContentTypes.Extension(contentType)}" : Path.GetFileName(request.FileName.Trim());
        ContractValidation.ValidateUpload(fileName, contentType, request.SizeBytes, options);
        if (!ingestionOptions.Value.TryNormalizeClayBody(request.ClayBody, out var clayBody))
            throw ApiProblems.Validation("clayBody", $"Clay body must be one of {string.Join(", ", ingestionOptions.Value.AllowedClayBodies)}.");

        // The back of the tile, with its code on it: its own file, checked like the front.
        string? backType = null;
        if (request.Back is { } back)
        {
            backType = UploadContentTypes.Resolve(back.ContentType, back.FileName);
            if (backType is null || !options.AllowedContentTypes.Contains(backType, StringComparer.OrdinalIgnoreCase))
                throw new ApiProblemException(400, "unsupported_content_type", "Unsupported photo type", "Send the back of the tile as a JPEG, PNG or WebP photo.");
            ContractValidation.ValidateUpload($"back{UploadContentTypes.Extension(backType)}", backType, back.SizeBytes, options);
        }

        var userId = User.GetUserId();
        // A capture code is needed unless the photo is of a tile task: anyone may photograph a tile marked done by its code.
        CaptureCode? code = null;
        if (request.CaptureCodeId is not null || request.TaskId is null)
            code = await db.CaptureCodes.AsNoTracking().Where(x => x.Id == request.CaptureCodeId && x.AssignedUserId == userId).Usable(DateTimeOffset.UtcNow).SingleOrDefaultAsync(cancellationToken)
                ?? throw new ApiProblemException(400, "capture_code_not_active", "Capture code not active", "This capture code is not active or is not assigned to you.");

        // The recipe on the tile, by id or by lab code. It must be assigned to this capture code or be the caller's own variation.
        Recipe? recipe = null;
        if (request.RecipeId is { } recipeId)
            recipe = await db.Recipes.Include(x => x.BaseGlaze).SingleOrDefaultAsync(x => x.Id == recipeId && x.IsActive, cancellationToken)
                ?? throw new ApiProblemException(400, "recipe_not_found", "Recipe not found", "That recipe does not exist or is not active.");
        else if (!string.IsNullOrWhiteSpace(request.RecipeCode))
        {
            recipe = await labCodes.FindByCodeAsync(request.RecipeCode, cancellationToken);
            if (recipe is null || !recipe.IsActive) throw new ApiProblemException(400, "recipe_code_not_found", "Recipe code not found", $"Recipe code '{request.RecipeCode}' was not found.");
        }

        // A photo taken for a tile task takes the task's recipe and clay body (docs/features/tile-tasks.md).
        var task = await tasks.ForUploadAsync(userId, request.TaskId, recipe, clayBody, DateTimeOffset.UtcNow, cancellationToken);
        if (task is not null) { recipe ??= task.Recipe; clayBody ??= task.ClayBody; }
        if (clayBody is null && ingestionOptions.Value.RequireClayBodyAtCapture)
            throw ApiProblems.Validation("clayBody", $"Choose the clay body the tile is made from: {string.Join(", ", ingestionOptions.Value.AllowedClayBodies)}.");
        if (recipe is not null && task is null && recipe.OwnerUserId != userId
            && !await db.CodeRecipes.AnyAsync(x => x.CaptureCodeId == code!.Id && x.RecipeId == recipe.Id, cancellationToken)
            && !await tasks.HasLiveTaskAsync(userId, recipe.Id, cancellationToken))
            throw new ApiProblemException(400, "recipe_not_permitted", "Recipe not permitted", $"{recipe.Code ?? recipe.Name} is not yours, not assigned to this capture code and not one of your tasks.");

        var uploadId = Guid.CreateVersion7();
        var entity = new Upload
        {
            Id = uploadId,
            OwnerUserId = userId,
            CaptureCodeId = code?.Id,
            RecipeId = recipe?.Id,
            Recipe = recipe,
            ClayBody = clayBody,
            TileTaskId = task?.Id,
            ObjectKey = ObjectKeyPolicy.UploadOriginal(uploadId, contentType),
            OriginalFileName = fileName,
            ContentType = contentType,
            DeclaredSizeBytes = request.SizeBytes,
            BackObjectKey = backType is null ? null : ObjectKeyPolicy.UploadBack(uploadId, backType),
            BackContentType = backType,
            BackDeclaredSizeBytes = request.Back?.SizeBytes,
            DeclaredSha256 = string.IsNullOrWhiteSpace(request.Sha256) ? null : request.Sha256.Trim().ToLowerInvariant(),
            Status = UploadStatus.AwaitingUpload
        };
        db.Uploads.Add(entity);
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException) when (task is not null)
        {
            throw ApiProblems.Conflict("task_changed", "This task was changed just now (reassigned or updated). Refresh your tasks and try again.");
        }
        return CreatedAtAction(nameof(Get), new { uploadId }, ToDto(entity, null));
    }

    [HttpGet("{uploadId:guid}")]
    public async Task<ActionResult<UploadDto>> Get(Guid uploadId, CancellationToken cancellationToken)
    {
        var upload = await db.Uploads.AsNoTracking().Include(x => x.ImageAsset).Include(x => x.Recipe).SingleOrDefaultAsync(x => x.Id == uploadId && x.OwnerUserId == User.GetUserId(), cancellationToken)
            ?? throw ApiProblems.NotFound("Upload");
        return Ok(await BuildAsync(upload, cancellationToken));
    }

    /// <summary>Fresh signed PUTs for the upload's key (and the back's, when it has one). Ask again on every attempt; links expire.</summary>
    [HttpPost("{uploadId:guid}/presign")]
    public async Task<ActionResult<PresignResponse>> Presign(Guid uploadId, CancellationToken cancellationToken)
    {
        var upload = await db.Uploads.AsNoTracking().SingleOrDefaultAsync(x => x.Id == uploadId && x.OwnerUserId == User.GetUserId(), cancellationToken) ?? throw ApiProblems.NotFound("Upload");
        if (upload.Status != UploadStatus.AwaitingUpload) throw ApiProblems.Conflict("invalid_upload_state", $"This upload is {upload.Status} and cannot be sent again.");
        var metadata = new Dictionary<string, string> { ["upload-id"] = upload.Id.ToString("D"), ["schema-version"] = ObjectKeyPolicy.SchemaVersion };
        var signed = await storage.CreatePutUrlAsync(upload.ObjectKey, upload.ContentType, metadata, cancellationToken);
        PresignedPart? back = null;
        if (upload.BackObjectKey is { } backKey)
        {
            var b = await storage.CreatePutUrlAsync(backKey, upload.BackContentType!, new Dictionary<string, string>(metadata) { ["side"] = "back" }, cancellationToken);
            back = new PresignedPart("PUT", b.Url, b.ObjectKey, b.ExpiresAt, b.Headers);
        }
        return Ok(new PresignResponse(upload.Id, "PUT", signed.Url, signed.ObjectKey, signed.ExpiresAt, signed.Headers, back));
    }

    /// <summary>Verifies the stored object and queues validation. Safe to repeat: a completed upload returns its current state.</summary>
    [HttpPost("{uploadId:guid}/complete")]
    public async Task<ActionResult<UploadDto>> Complete(Guid uploadId, [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] CompleteUploadRequest? request, CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();
        var upload = await db.Uploads.Include(x => x.ImageAsset).Include(x => x.Recipe).SingleOrDefaultAsync(x => x.Id == uploadId && x.OwnerUserId == userId, cancellationToken) ?? throw ApiProblems.NotFound("Upload");
        if (upload.Status is not (UploadStatus.AwaitingUpload or UploadStatus.Created or UploadStatus.Cancelled)) return Ok(await BuildAsync(upload, cancellationToken));
        if (upload.Status != UploadStatus.AwaitingUpload) throw ApiProblems.Conflict("invalid_upload_state", $"This upload is {upload.Status} and cannot be completed.");

        var stored = await storage.HeadAsync(upload.ObjectKey, cancellationToken)
            ?? throw new ApiProblemException(409, "object_not_found", "Photo not received", "The photo has not reached storage yet. Send it, then complete the upload.");
        if (stored.Size != upload.DeclaredSizeBytes) throw new ApiProblemException(400, "size_mismatch", "Size mismatch", "The stored photo is not the size that was declared.");
        if (!string.IsNullOrWhiteSpace(stored.ContentType) && !string.Equals(stored.ContentType, upload.ContentType, StringComparison.OrdinalIgnoreCase))
            throw new ApiProblemException(400, "content_type_mismatch", "Type mismatch", "The stored photo's content type does not match the declared type.");
        if (!string.IsNullOrWhiteSpace(upload.DeclaredSha256) && !string.IsNullOrWhiteSpace(request?.Sha256) && !string.Equals(upload.DeclaredSha256, request.Sha256.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new ApiProblemException(400, "checksum_mismatch", "Checksum mismatch", "The checksum does not match the one declared when the upload was created.");
        if (upload.BackObjectKey is { } backKey)
        {
            var back = await storage.HeadAsync(backKey, cancellationToken)
                ?? throw new ApiProblemException(409, "back_not_found", "Back photo not received", "The photo of the back of the tile has not reached storage yet. Send it, then complete the upload.");
            if (back.Size != upload.BackDeclaredSizeBytes) throw new ApiProblemException(400, "size_mismatch", "Size mismatch", "The stored photo of the back is not the size that was declared.");
        }

        upload.Status = UploadStatus.Uploaded;
        upload.ActualSizeBytes = stored.Size;
        upload.ETag = stored.ETag;
        upload.UploadedAt = DateTimeOffset.UtcNow;
        upload.CompletedAt = DateTimeOffset.UtcNow;
        upload.ConcurrencyStamp = Guid.NewGuid();
        // Asset identity, recipe link, and the validation job commit with the state change or not at all.
        upload.ImageAsset = await ingestion.RegisterUploadAsync(upload, stored, cancellationToken);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex is DbUpdateConcurrencyException || ex.InnerException is Npgsql.PostgresException { SqlState: "23505" })
        {
            // Lost a race with a concurrent completion (stamp mismatch or the unique ImageAssets.UploadId index); return the winner's state.
            db.ChangeTracker.Clear();
            var current = await db.Uploads.AsNoTracking().Include(x => x.ImageAsset).Include(x => x.Recipe).SingleAsync(x => x.Id == uploadId, cancellationToken);
            return Ok(await BuildAsync(current, cancellationToken));
        }
        logger.LogInformation("Upload {UploadId} completed for user {UserId}; image {ImageId} queued for validation", uploadId, userId, upload.ImageAsset.Id);
        return Ok(await BuildAsync(upload, cancellationToken));
    }

    /// <summary>Withdraws an upload that has not been completed. Idempotent; a completed upload is left alone.</summary>
    [HttpPost("{uploadId:guid}/cancel")]
    public async Task<IActionResult> Cancel(Guid uploadId, CancellationToken cancellationToken)
    {
        var upload = await db.Uploads.SingleOrDefaultAsync(x => x.Id == uploadId && x.OwnerUserId == User.GetUserId(), cancellationToken) ?? throw ApiProblems.NotFound("Upload");
        if (upload.Status is UploadStatus.Cancelled) return NoContent();
        if (upload.Status is not (UploadStatus.AwaitingUpload or UploadStatus.Created or UploadStatus.Failed))
            throw ApiProblems.Conflict("invalid_upload_state", $"This upload is {upload.Status} and can no longer be cancelled.");
        upload.Status = UploadStatus.Cancelled;
        upload.ConcurrencyStamp = Guid.NewGuid();
        await db.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    private async Task<UploadDto> BuildAsync(Upload upload, CancellationToken cancellationToken)
    {
        var visible = UploadContentTypes.HasObject(upload.Status);
        return ToDto(upload, visible ? await storage.CreateGetUrlAsync(upload.ObjectKey, cancellationToken) : null,
            visible ? await UploadContentTypes.ThumbnailUrlAsync(upload, storage, cancellationToken) : null,
            visible ? await UploadContentTypes.BackUrlAsync(upload, storage, cancellationToken) : null);
    }

    public static UploadDto ToDto(Upload u, Uri? url, Uri? thumbnailUrl = null, Uri? backUrl = null) => new(u.Id, u.Status, u.ObjectKey, u.OriginalFileName, u.ContentType, u.DeclaredSizeBytes, u.ActualSizeBytes,
        u.FailureReason, u.ReviewNote, u.CreatedAt, u.UploadedAt, u.CompletedAt, u.ReviewedAt, url,
        u.RecipeId, u.Recipe?.Name, u.Recipe?.Code, u.ImageAsset?.Id, NextActionFor(u.Status), thumbnailUrl, u.ClayBody, u.TileTaskId, backUrl);

    private static string? NextActionFor(UploadStatus status) => status switch
    {
        UploadStatus.Created or UploadStatus.AwaitingUpload => "presign_and_put",
        UploadStatus.Uploaded or UploadStatus.Validating => "validation",
        UploadStatus.NeedsReview => "admin_review",
        UploadStatus.Approved or UploadStatus.Indexing => "indexing",
        _ => null
    };
}

public static class UploadContentTypes
{
    private static readonly Dictionary<string, string> ByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".png"] = "image/png", [".webp"] = "image/webp", [".heic"] = "image/heic", [".heif"] = "image/heif"
    };

    /// <summary>The declared type, or the type implied by the file name when the client sends none (a Blob's type can be empty).</summary>
    public static string? Resolve(string? contentType, string? fileName)
    {
        var declared = contentType?.Split(';')[0].Trim().ToLowerInvariant();
        if (!string.IsNullOrEmpty(declared)) return declared == "image/jpg" ? "image/jpeg" : declared;
        var ext = Path.GetExtension(fileName ?? string.Empty);
        return ByExtension.GetValueOrDefault(ext);
    }

    /// <summary>The 400 px WebP derivative when it exists, so lists need not load full-size originals.</summary>
    public static async Task<Uri?> ThumbnailUrlAsync(Upload upload, IR2ObjectStorage storage, CancellationToken cancellationToken) =>
        upload.ImageAsset?.ThumbnailKey is { } key ? await storage.CreateGetUrlAsync(key, cancellationToken) : null;

    /// <summary>The back of the tile, when one was sent with the photo.</summary>
    public static async Task<Uri?> BackUrlAsync(Upload upload, IR2ObjectStorage storage, CancellationToken cancellationToken) =>
        upload.BackObjectKey is { } key ? await storage.CreateGetUrlAsync(key, cancellationToken) : null;

    public static string Extension(string contentType) => contentType switch { "image/png" => ".png", "image/webp" => ".webp", _ => ".jpg" };

    /// <summary>States in which the original is in storage and can be shown (a rejected photo is still visible to its owner and to reviewers).</summary>
    public static bool HasObject(UploadStatus status) => status is UploadStatus.Uploaded or UploadStatus.Validating or UploadStatus.NeedsReview or UploadStatus.Approved
        or UploadStatus.Rejected or UploadStatus.Indexing or UploadStatus.Indexed;
}
