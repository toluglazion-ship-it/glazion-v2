using Glazion.Api.Contracts;
using Glazion.Api.Data;
using Glazion.Api.Domain;
using Glazion.Api.Infrastructure;
using Glazion.Api.Retrieval;
using Glazion.Api.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Glazion.Api.Controllers;

/// <summary>The caller's own tile tasks, for the Capture app (docs/features/tile-tasks.md). Cancelled tasks are never shown.</summary>
[ApiController]
[Route("api/v2/me/tasks")]
[Authorize]
public sealed class MyTasksController(GlazionDbContext db, TileTaskService tasks, MatchCheckService recipes) : ControllerBase
{
    /// <summary>
    /// To make and waiting for a photo first (soonest due first), then photos in review, then approved. stage narrows it.
    /// Each task carries its recipe (base, additions, colorants), so the app can show it and swipe from one to the next.
    /// A plain array (at most 500) unless cursor or pageSize is sent, which returns PagedResult.
    /// </summary>
    [HttpGet]
    [OptInPaging(typeof(TileTaskDto))]
    public async Task<IActionResult> List([FromQuery] TileTaskStage? stage, [FromQuery] string? cursor, [FromQuery] int? pageSize, CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();
        var rows = TileTaskService.Rows(db.TileTasks.AsNoTracking().Where(t => t.AssignedUserId == userId && t.CancelledAt == null));
        if (stage is { } s) rows = TileTaskService.WhereStage(rows, s);
        var ordered = TileTaskService.OrderedForParticipant(rows);
        if (Paging.Requested(cursor, pageSize))
            return Ok(await Paging.PageAsync(ordered, cursor, pageSize, async page => await WithRecipesAsync(await tasks.MapAsync(page, false, cancellationToken), cancellationToken), cancellationToken));
        return Ok(await WithRecipesAsync(await tasks.MapAsync(await ordered.Take(500).ToListAsync(cancellationToken), false, cancellationToken), cancellationToken));
    }

    [HttpGet("{taskId:guid}")]
    public async Task<TileTaskDto> Get(Guid taskId, CancellationToken cancellationToken) =>
        (await WithRecipesAsync([await tasks.GetAsync(taskId, User.GetUserId(), false, cancellationToken)], cancellationToken))[0];

    /// <summary>Shows the recipe (in the response) and locks it, so it can't change while the tile is made. Saying it twice changes nothing.</summary>
    [HttpPost("{taskId:guid}/start")]
    public async Task<TileTaskDto> Start(Guid taskId, CancellationToken cancellationToken) =>
        (await WithRecipesAsync([await tasks.StartAsync(User.GetUserId(), taskId, cancellationToken)], cancellationToken))[0];

    /// <summary>The tile is made: it goes to an admin to check before it is photographed. Only after Start. Saying it twice changes nothing.</summary>
    [HttpPost("{taskId:guid}/done")]
    public async Task<TileTaskDto> MarkDone(Guid taskId, CancellationToken cancellationToken) =>
        (await WithRecipesAsync([await tasks.MarkDoneAsync(User.GetUserId(), taskId, cancellationToken)], cancellationToken))[0];

    /// <summary>Takes back "done" (tapped by mistake). Refused once an admin has checked it.</summary>
    [HttpDelete("{taskId:guid}/done")]
    public async Task<TileTaskDto> UndoDone(Guid taskId, CancellationToken cancellationToken) =>
        (await WithRecipesAsync([await tasks.UndoDoneAsync(User.GetUserId(), taskId, cancellationToken)], cancellationToken))[0];

    private async Task<IReadOnlyList<TileTaskDto>> WithRecipesAsync(IReadOnlyList<TileTaskDto> list, CancellationToken cancellationToken)
    {
        var ids = list.Select(t => t.RecipeId).Distinct().ToList();
        var byId = await db.Recipes.AsNoTracking().Include(r => r.BaseGlaze).Where(r => ids.Contains(r.Id)).ToDictionaryAsync(r => r.Id, cancellationToken);
        // The recipe is shown only once they press Start: until then the admin may still change it.
        return list.Select(t => t.Stage != TileTaskStage.Assigned && byId.TryGetValue(t.RecipeId, out var r) ? t with { Recipe = recipes.RecipeFor(r) } : t).ToList();
    }
}
