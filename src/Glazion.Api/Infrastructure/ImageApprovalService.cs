using Glazion.Api.Data;
using Glazion.Api.Domain;
using Glazion.Api.Indexing;
using Microsoft.EntityFrameworkCore;

namespace Glazion.Api.Infrastructure;

public sealed record ApprovalOutcome(bool Succeeded, int StatusCode, string? Error)
{
    public Glazion.Api.Contracts.ApiProblemException ToProblem() => StatusCode == 400
        ? new(400, "invalid_recipe", "Recipe not attached", Error ?? "The recipe could not be attached.")
        : new(StatusCode, "invalid_review_state", "Cannot review", Error ?? "This image cannot be reviewed in its current state.");
}

/// <summary>
/// The one place an image changes approval state, whichever path created it (mobile upload or bulk import).
/// Approval is gated on validation, may attach and verify a recipe, bumps SourceVersion, mirrors the state onto
/// the Upload row when there is one, and enqueues the index sync in the same transaction (IMAGE-06).
/// </summary>
public sealed class ImageApprovalService(GlazionDbContext db, IImageIngestionService ingestion, ILogger<ImageApprovalService> logger)
{
    public async Task<ApprovalOutcome> ApproveAsync(ImageAsset asset, Guid adminId, Guid? recipeId, string? note, CancellationToken cancellationToken)
    {
        if (asset.ApprovalStatus == ApprovalStatus.Approved && recipeId is null && string.IsNullOrWhiteSpace(note)) return new ApprovalOutcome(true, 204, null);
        if (asset.QualityStatus == QualityStatus.Pending) return new ApprovalOutcome(false, 409, "Image validation has not completed yet. Approve after the validation job finishes.");
        if (asset.QualityStatus == QualityStatus.Failed) return new ApprovalOutcome(false, 409, "Image failed validation and cannot be approved.");
        if (asset.Upload is { } upload && upload.Status is UploadStatus.Cancelled or UploadStatus.Failed) return new ApprovalOutcome(false, 409, $"Upload is {upload.Status} and cannot be approved.");

        if (recipeId is { } id)
        {
            try { await ingestion.AttachRecipeAsync(asset, id, adminId, verified: true, cancellationToken); }
            catch (InvalidOperationException ex) { return new ApprovalOutcome(false, 400, ex.Message); }
        }
        else
        {
            // Approving the image confirms the lab code that was declared at capture (or auto-linked from the capture code).
            foreach (var link in asset.Recipes.Where(x => !x.Verified)) link.Verified = true;
        }

        var now = DateTimeOffset.UtcNow;
        asset.ApprovalStatus = ApprovalStatus.Approved;
        asset.ApprovedAt = now;
        asset.ApprovedBy = adminId;
        asset.SourceVersion++;
        asset.UpdatedAt = now;
        asset.ConcurrencyStamp = Guid.NewGuid();
        if (asset.Upload is { } u)
        {
            // Already searchable images stay Indexed; the re-sync below refreshes them.
            if (u.Status is not (UploadStatus.Indexing or UploadStatus.Indexed)) u.Status = UploadStatus.Approved;
            if (!string.IsNullOrWhiteSpace(note)) u.ReviewNote = Trim(note);
            u.ReviewedAt = now;
            u.ReviewedBy = adminId;
            u.ConcurrencyStamp = Guid.NewGuid();
        }
        if (asset.ImportRecord is { } r && (r.Status == ImportRecordStatus.NeedsReview || r.FailureCode == "rejected_by_admin"))
        {
            r.Status = ImportRecordStatus.Validated; // includes re-approval after an admin rejection
            r.FailureCode = null;
            r.FailureDetail = null;
        }

        await IndexJobs.EnqueueUpsertAsync(db, asset.Id, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Image {ImageId} approved by {AdminId} (sourceVersion {SourceVersion}); index sync queued", asset.Id, adminId, asset.SourceVersion);
        return new ApprovalOutcome(true, 204, null);
    }

    public async Task<ApprovalOutcome> RejectAsync(ImageAsset asset, Guid adminId, string? reason, CancellationToken cancellationToken)
    {
        if (asset.Upload is { Status: UploadStatus.Cancelled } up) return new ApprovalOutcome(false, 409, $"Upload is {up.Status} and cannot be rejected.");
        var wasApproved = asset.ApprovalStatus == ApprovalStatus.Approved;
        var note = RejectionNote(reason);
        var now = DateTimeOffset.UtcNow;
        asset.ApprovalStatus = ApprovalStatus.Rejected;
        asset.SourceVersion++;
        asset.UpdatedAt = now;
        asset.ConcurrencyStamp = Guid.NewGuid();
        if (asset.Upload is { } u) { u.Status = UploadStatus.Rejected; u.ReviewNote = note; u.ReviewedAt = now; u.ReviewedBy = adminId; u.ConcurrencyStamp = Guid.NewGuid(); }
        if (asset.ImportRecord is { } r) { r.Status = ImportRecordStatus.Failed; r.FailureCode = "rejected_by_admin"; r.FailureDetail = note; }

        if (wasApproved) await IndexJobs.EnqueueRemoveAsync(db, asset.Id, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Image {ImageId} rejected by {AdminId}{Removal}", asset.Id, adminId, wasApproved ? "; index removal queued" : string.Empty);
        return new ApprovalOutcome(true, 204, null);
    }

    /// <summary>The reviewer's reason as shown to the participant (UploadDto.reviewNote).</summary>
    public static string RejectionNote(string? reason) => string.IsNullOrWhiteSpace(reason) ? "Rejected by admin." : Trim(reason);

    private static string Trim(string value) { var t = value.Trim(); return t.Length > 2000 ? t[..2000] : t; }

    public static IQueryable<ImageAsset> ForApproval(GlazionDbContext db) => db.ImageAssets.Include(x => x.Upload).Include(x => x.ImportRecord).Include(x => x.Recipes);
}
