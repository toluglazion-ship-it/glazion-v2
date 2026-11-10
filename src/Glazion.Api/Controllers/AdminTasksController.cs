using Glazion.Api.Contracts;
using Glazion.Api.Data;
using Glazion.Api.Domain;
using Glazion.Api.Infrastructure;
using Glazion.Api.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Glazion.Api.Controllers;

/// <summary>Tile tasks: who is making which recipe on which clay body, by when, and how far it has got (docs/features/tile-tasks.md).</summary>
[ApiController]
[Route("api/v2/admin/tasks")]
[Authorize(Roles = "Admin")]
public sealed class AdminTasksController(GlazionDbContext db, TileTaskService tasks, IOptions<IngestionOptions> ingestion) : ControllerBase
{
    /// <summary>
    /// Tasks, soonest due first. stage: Assigned, Started, Done, AwaitingPhoto, InReview, Approved, Cancelled (all but
    /// Cancelled when omitted). overdue=true: past their due date and not made yet. q matches the recipe's code or name and the
    /// participant's username.
    /// </summary>
    [HttpGet]
    public async Task<PagedResult<TileTaskDto>> List([FromQuery] TileTaskStage? stage, [FromQuery] Guid? userId, [FromQuery] Guid? recipeId,
        [FromQuery] string? clayBody, [FromQuery] bool? overdue, [FromQuery] string? q, [FromQuery] string? cursor, [FromQuery] int? pageSize,
        CancellationToken cancellationToken)
    {
        var scope = Scope(userId, recipeId, clayBody, q);
        var rows = TileTaskService.Rows(scope);
        rows = stage is { } s ? TileTaskService.WhereStage(rows, s) : rows.Where(r => r.Task.CancelledAt == null);
        if (overdue == true) rows = TileTaskService.WhereOverdue(rows, TileTaskService.Today);
        return await Paging.PageAsync(TileTaskService.Ordered(rows), cursor, pageSize, page => tasks.MapAsync(page, false, cancellationToken), cancellationToken);
    }

    /// <summary>How many tasks are at each stage, for the same filters as the list (without stage and overdue).</summary>
    [HttpGet("summary")]
    public Task<TileTaskSummaryDto> Summary([FromQuery] Guid? userId, [FromQuery] Guid? recipeId, [FromQuery] string? clayBody, [FromQuery] string? q,
        CancellationToken cancellationToken) =>
        tasks.SummaryAsync(Scope(userId, recipeId, clayBody, q), cancellationToken);

    /// <summary>One task with its history.</summary>
    [HttpGet("{taskId:guid}")]
    public Task<TileTaskDto> Get(Guid taskId, CancellationToken cancellationToken) => tasks.GetAsync(taskId, null, true, cancellationToken);

    /// <summary>
    /// Gives a participant recipes to make: one task per recipe and clay body. Tiles that already have a live task are
    /// skipped and listed, so sending the same request twice creates nothing new.
    /// </summary>
    [HttpPost]
    public Task<AssignTileTasksResponse> Assign(AssignTileTasksRequest request, CancellationToken cancellationToken) =>
        tasks.AssignAsync(request, User.GetUserId(), cancellationToken);

    /// <summary>Changes the due date or the note. Refused once the task is cancelled.</summary>
    [HttpPatch("{taskId:guid}")]
    public Task<TileTaskDto> Update(Guid taskId, UpdateTileTaskRequest request, CancellationToken cancellationToken) =>
        tasks.UpdateAsync(taskId, request, User.GetUserId(), cancellationToken);

    /// <summary>
    /// Moves the task to another participant, e.g. when the first did not come in. Only while no photo of it is in review
    /// or approved. The new participant starts from "to make"; the recipe's code stays the same.
    /// </summary>
    [HttpPost("{taskId:guid}/reassign")]
    public async Task<TileTaskDto> Reassign(Guid taskId, ReassignTileTaskRequest request, CancellationToken cancellationToken)
    {
        var result = await tasks.ReassignAsync([taskId], request.AssignedUserId, request.DueOn, request.Reason, request.Version, single: true, User.GetUserId(), cancellationToken);
        return result.Reassigned[0];
    }

    /// <summary>Moves several tasks at once (all of one participant's open tasks, say). Tasks that cannot move are listed with why.</summary>
    [HttpPost("reassign")]
    public Task<ReassignTileTasksResponse> ReassignMany(ReassignTileTasksRequest request, CancellationToken cancellationToken) =>
        tasks.ReassignAsync(request.TaskIds ?? [], request.AssignedUserId, request.DueOn, request.Reason, null, single: false, User.GetUserId(), cancellationToken);

    /// <summary>Approves made tiles (stage Done) after checking them: each can then be fired and photographed. The rest are skipped, with why.</summary>
    [HttpPost("approve")]
    public Task<TileTaskBatchResponse> Approve(ReviewTileTasksRequest request, CancellationToken cancellationToken) =>
        tasks.ApproveAsync(request.TaskIds, User.GetUserId(), cancellationToken);

    /// <summary>Sends made tiles back to their participant to make again, with a reason they see (required).</summary>
    [HttpPost("return")]
    public Task<TileTaskBatchResponse> Return(ReviewTileTasksRequest request, CancellationToken cancellationToken) =>
        tasks.ReturnAsync(request.TaskIds, request.Reason, User.GetUserId(), cancellationToken);

    /// <summary>Withdraws (deletes) the task. Only while no photo of it is in review or approved; the tile can then be assigned again, or the task restored.</summary>
    [HttpPost("{taskId:guid}/cancel")]
    public Task<TileTaskDto> Cancel(Guid taskId, CancelTileTaskRequest? request, CancellationToken cancellationToken) =>
        tasks.CancelAsync(taskId, request ?? new CancelTileTaskRequest(null, null), User.GetUserId(), cancellationToken);

    /// <summary>Everything that happened to one participant's work, newest first (tasks and photos). limit: 1..500, default 100.</summary>
    [HttpGet("activity/{userId:guid}")]
    public Task<IReadOnlyList<ParticipantActivityDto>> Activity(Guid userId, [FromQuery] int? limit, CancellationToken cancellationToken) =>
        tasks.ActivityAsync(userId, limit, cancellationToken);

    /// <summary>Brings a cancelled (deleted) task back to the participant it was with, at the stage it had reached.</summary>
    [HttpPost("{taskId:guid}/restore")]
    public Task<TileTaskDto> Restore(Guid taskId, CancellationToken cancellationToken) => tasks.RestoreAsync(taskId, User.GetUserId(), cancellationToken);

    private IQueryable<TileTask> Scope(Guid? userId, Guid? recipeId, string? clayBody, string? q)
    {
        var scope = db.TileTasks.AsNoTracking();
        if (userId is { } u) scope = scope.Where(t => t.AssignedUserId == u);
        if (recipeId is { } r) scope = scope.Where(t => t.RecipeId == r);
        if (!string.IsNullOrWhiteSpace(clayBody))
        {
            if (!ingestion.Value.TryNormalizeClayBody(clayBody, out var body) || body is null)
                throw ApiProblems.Validation("clayBody", $"clayBody must be one of {string.Join(", ", ingestion.Value.AllowedClayBodies)}.");
            scope = scope.Where(t => t.ClayBody == body);
        }
        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim().ToLowerInvariant();
            scope = scope.Where(t => t.Recipe.Name.ToLower().Contains(term) || (t.Recipe.Code != null && t.Recipe.Code.ToLower().Contains(term))
                                     || t.AssignedUser.Username.ToLower().Contains(term));
        }
        return scope;
    }
}
