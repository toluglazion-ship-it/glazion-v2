using Glazion.Api.Contracts;
using Glazion.Api.Data;
using Glazion.Api.Domain;
using Glazion.Api.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.EntityFrameworkCore;

namespace Glazion.Api.Controllers;

[ApiController]
[Route("api/v2/admin/uploads")]
[Authorize(Roles = "Admin")]
public sealed class AdminUploadsController(GlazionDbContext db, IR2ObjectStorage storage, ImageApprovalService approvals) : ControllerBase
{
    [HttpGet]
    public Task<PagedResult<AdminUploadDto>> List([FromQuery] string? cursor, [FromQuery] int? pageSize, [FromQuery] UploadStatus? status, [FromQuery] Guid? ownerUserId, CancellationToken cancellationToken)
    {
        var query = db.Uploads.AsNoTracking().Include(x => x.ImageAsset).Include(x => x.Recipe).Include(x => x.OwnerUser).Include(x => x.TileTask).ThenInclude(x => x!.AssignedUser).AsSplitQuery().AsQueryable();
        if (status is not null) query = query.Where(x => x.Status == status);
        if (ownerUserId is not null) query = query.Where(x => x.OwnerUserId == ownerUserId);
        return Paging.PageAsync(query.OrderByDescending(x => x.CreatedAt).ThenBy(x => x.Id), cursor, pageSize, async rows =>
        {
            var result = new List<AdminUploadDto>(rows.Count);
            foreach (var u in rows)
            {
                var visible = UploadContentTypes.HasObject(u.Status);
                var url = visible ? await storage.CreateGetUrlAsync(u.ObjectKey, cancellationToken) : null;
                var thumbnail = visible ? await UploadContentTypes.ThumbnailUrlAsync(u, storage, cancellationToken) : null;
                var back = visible ? await UploadContentTypes.BackUrlAsync(u, storage, cancellationToken) : null;
                result.Add(new AdminUploadDto(u.Id, u.OwnerUserId, u.OwnerUser?.Username, u.CaptureCodeId, u.Status, u.ObjectKey, u.OriginalFileName, u.ContentType,
                    u.DeclaredSizeBytes, u.ActualSizeBytes, u.FailureReason, u.ReviewNote, u.CreatedAt, u.UploadedAt, u.ReviewedAt, url,
                    u.RecipeId, u.Recipe?.Name, u.Recipe?.Code, u.ImageAsset?.Id, u.ImageAsset?.QualityStatus, thumbnail, u.ImageAsset?.ClayBody ?? u.ClayBody, back, u.TileTask?.AssignedUser?.Username));
            }
            return (IReadOnlyList<AdminUploadDto>)result;
        }, cancellationToken);
    }

    /// <summary>Approval requires the worker to have inspected the object; it may attach the recipe the curator confirms and queues indexing.</summary>
    [HttpPost("{uploadId:guid}/approve")]
    public async Task<IActionResult> Approve(Guid uploadId, [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] ApproveUploadRequest? request, CancellationToken cancellationToken)
    {
        var upload = await db.Uploads.AsNoTracking().SingleOrDefaultAsync(x => x.Id == uploadId, cancellationToken) ?? throw ApiProblems.NotFound("Upload");
        var asset = await ImageApprovalService.ForApproval(db).SingleOrDefaultAsync(x => x.UploadId == uploadId, cancellationToken);
        if (asset is null)
            throw ApiProblems.Conflict("not_ready_for_review", upload.Status == UploadStatus.AwaitingUpload ? "This upload has not been completed yet." : "The photo is still being checked. Approve it once validation has finished.");
        var outcome = await approvals.ApproveAsync(asset, User.GetUserId(), request?.RecipeId, request?.Note, cancellationToken);
        return outcome.Succeeded ? NoContent() : throw outcome.ToProblem();
    }

    [HttpPost("{uploadId:guid}/reject")]
    public async Task<IActionResult> Reject(Guid uploadId, [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] RejectUploadRequest? request, CancellationToken cancellationToken)
    {
        var asset = await ImageApprovalService.ForApproval(db).SingleOrDefaultAsync(x => x.UploadId == uploadId, cancellationToken);
        if (asset is not null)
        {
            var outcome = await approvals.RejectAsync(asset, User.GetUserId(), request?.Reason, cancellationToken);
            return outcome.Succeeded ? NoContent() : throw outcome.ToProblem();
        }
        // No asset yet (never completed): reject the bare upload row.
        var upload = await db.Uploads.SingleOrDefaultAsync(x => x.Id == uploadId, cancellationToken) ?? throw ApiProblems.NotFound("Upload");
        if (upload.Status is UploadStatus.Rejected) return NoContent();
        if (upload.Status is UploadStatus.Approved or UploadStatus.Indexing or UploadStatus.Indexed or UploadStatus.Cancelled)
            throw ApiProblems.Conflict("invalid_upload_state", $"This upload is {upload.Status} and cannot be rejected.");
        upload.Status = UploadStatus.Rejected;
        upload.ReviewNote = ImageApprovalService.RejectionNote(request?.Reason);
        upload.ReviewedAt = DateTimeOffset.UtcNow;
        upload.ReviewedBy = User.GetUserId();
        upload.ConcurrencyStamp = Guid.NewGuid();
        await db.SaveChangesAsync(cancellationToken);
        return NoContent();
    }
}
