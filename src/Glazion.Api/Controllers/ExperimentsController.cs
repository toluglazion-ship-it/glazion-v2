using Glazion.Api.Data;
using Glazion.Api.Experiments;
using Glazion.Api.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Glazion.Api.Controllers;

/// <summary>Cup experiments, set up and watched by an admin (docs/features/cup-experiments.md). The admin can also do the participant's steps.</summary>
[ApiController]
[Route("api/v2/admin/experiments")]
[Authorize(Roles = "Admin")]
public sealed class AdminExperimentsController(ExperimentService experiments) : ControllerBase
{
    /// <summary>Newest first (at most 200). participantUserId: one participant's. baseCode: those of one base (its stages, in the base form). deleted=true: the deleted ones, to restore.</summary>
    [HttpGet]
    public Task<IReadOnlyList<ExperimentDto>> List([FromQuery] Guid? participantUserId, [FromQuery] bool? deleted, CancellationToken cancellationToken, [FromQuery] string? baseCode = null) =>
        experiments.ListAsync(participantUserId, deleted == true, cancellationToken, baseCode);

    [HttpGet("{id:guid}")]
    public Task<ExperimentDto> Get(Guid id, CancellationToken cancellationToken) => experiments.GetAsync(id, null, cancellationToken);

    [HttpPost]
    public async Task<ActionResult<ExperimentDto>> Create(CreateExperimentRequest request, CancellationToken cancellationToken)
    {
        var created = await experiments.CreateAsync(request, User.GetUserId(), cancellationToken);
        return Created($"/api/v2/admin/experiments/{created.Id}", created);
    }

    /// <summary>Name, the stages not started yet (a started one can only gain levels), the tiles per step and their clay bodies, the reserve; before the first step also the cups, the glaze in them and who mixes them.</summary>
    [HttpPatch("{id:guid}")]
    public Task<ExperimentDto> Update(Guid id, UpdateExperimentRequest request, CancellationToken cancellationToken) => experiments.UpdateAsync(id, request, cancellationToken);

    /// <summary>Takes it away from the participant; its recipes and tiles stay. Restorable.</summary>
    [HttpDelete("{id:guid}")]
    public Task<ExperimentDto> Delete(Guid id, CancellationToken cancellationToken) => experiments.DeleteAsync(id, false, cancellationToken);

    [HttpPost("{id:guid}/restore")]
    public Task<ExperimentDto> Restore(Guid id, CancellationToken cancellationToken) => experiments.DeleteAsync(id, true, cancellationToken);

    [HttpPost("{id:guid}/steps/start")]
    public Task<ExperimentDto> Start(Guid id, CancellationToken cancellationToken) => experiments.StartStepAsync(id, User.GetUserId(), true, cancellationToken);

    [HttpPost("{id:guid}/steps/cancel")]
    public Task<ExperimentDto> CancelStep(Guid id, CancellationToken cancellationToken) => experiments.CancelStepAsync(id, User.GetUserId(), true, cancellationToken);

    [HttpPost("{id:guid}/steps/dipped")]
    public Task<ExperimentDto> Dipped(Guid id, DippedRequest request, CancellationToken cancellationToken) => experiments.DippedAsync(id, request, User.GetUserId(), true, cancellationToken);

    [HttpPost("{id:guid}/cups/{cup:int}/refill")]
    public Task<ExperimentDto> Refill(Guid id, int cup, RefillRequest request, CancellationToken cancellationToken) =>
        experiments.RefillAsync(id, cup, request, User.GetUserId(), true, cancellationToken);
}

/// <summary>
/// The participant's own cup experiments (Capture app): start a step to see the grams for every cup and their codes,
/// dip, weigh one cup and say so; the next step opens straight away. Fresh glaze for a cup that ran low, with the admin.
/// </summary>
[ApiController]
[Route("api/v2/me/experiments")]
[Authorize]
public sealed class MyExperimentsController(ExperimentService experiments, GlazionDbContext db) : ControllerBase
{
    /// <summary>Newest first.</summary>
    [HttpGet]
    public async Task<IReadOnlyList<ExperimentDto>> List(CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();
        var ids = await db.Experiments.AsNoTracking().Where(x => x.ParticipantUserId == userId && x.DeletedAt == null)
            .OrderByDescending(x => x.CreatedAt).Select(x => x.Id).ToListAsync(cancellationToken);
        var result = new List<ExperimentDto>();
        foreach (var id in ids) result.Add(await experiments.GetAsync(id, userId, cancellationToken));
        return result;
    }

    [HttpGet("{id:guid}")]
    public Task<ExperimentDto> Get(Guid id, CancellationToken cancellationToken) => experiments.GetAsync(id, User.GetUserId(), cancellationToken);

    [HttpPost("{id:guid}/steps/start")]
    public Task<ExperimentDto> Start(Guid id, CancellationToken cancellationToken) => experiments.StartStepAsync(id, User.GetUserId(), false, cancellationToken);

    [HttpPost("{id:guid}/steps/cancel")]
    public Task<ExperimentDto> CancelStep(Guid id, CancellationToken cancellationToken) => experiments.CancelStepAsync(id, User.GetUserId(), false, cancellationToken);

    [HttpPost("{id:guid}/steps/dipped")]
    public Task<ExperimentDto> Dipped(Guid id, DippedRequest request, CancellationToken cancellationToken) => experiments.DippedAsync(id, request, User.GetUserId(), false, cancellationToken);

    [HttpPost("{id:guid}/cups/{cup:int}/refill")]
    public Task<ExperimentDto> Refill(Guid id, int cup, RefillRequest request, CancellationToken cancellationToken) =>
        experiments.RefillAsync(id, cup, request, User.GetUserId(), false, cancellationToken);
}
