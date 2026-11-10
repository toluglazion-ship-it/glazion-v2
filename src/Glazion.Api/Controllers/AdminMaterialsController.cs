using Glazion.Api.Chemistry;
using Glazion.Api.Contracts;
using Glazion.Api.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Glazion.Api.Controllers;

/// <summary>
/// Material corrections and additions from the admin app (docs/features/materials-admin.md). The list everyone picks from is
/// GET /api/v2/materials; these routes change what it holds.
/// </summary>
[ApiController]
[Route("api/v2/admin/materials")]
[Authorize(Roles = "Admin")]
public sealed class AdminMaterialsController(MaterialCatalog catalog) : ControllerBase
{
    /// <summary>Every administrator override, with the materials version calculations now use.</summary>
    [HttpGet("overrides")]
    public async Task<PagedResult<MaterialOverrideResponse>> Overrides([FromQuery] string? cursor, [FromQuery] int? pageSize, CancellationToken cancellationToken) =>
        Paging.Page(await catalog.ListAsync(cancellationToken), cursor, pageSize);

    /// <summary>materials.json entries sharing one id; conflicting groups first. Settle one with PUT on its id.</summary>
    [HttpGet("duplicates")]
    public async Task<PagedResult<MaterialDuplicateGroup>> Duplicates([FromQuery] string? cursor, [FromQuery] int? pageSize, CancellationToken cancellationToken) =>
        Paging.Page(await catalog.DuplicatesAsync(cancellationToken), cursor, pageSize);

    /// <summary>
    /// Sets the analysis of one material id: corrects a materials.json material (all its duplicate entries) or adds a
    /// new one. The id is the name's slug ("CP#7 Ball Clay" → "cp-7-ball-clay").
    /// </summary>
    [HttpPut("{materialId}")]
    public async Task<ActionResult<MaterialOverrideResponse>> Upsert(string materialId, UpsertMaterialRequest request, CancellationToken cancellationToken) =>
        Ok(await catalog.UpsertAsync(materialId.Trim().ToLowerInvariant(), request, User.GetUserId(), cancellationToken));

    /// <summary>Removes an override: back to the materials.json analysis, or gone if it was a new material.</summary>
    [HttpDelete("{materialId}")]
    public async Task<IActionResult> Delete(string materialId, CancellationToken cancellationToken) =>
        await catalog.DeleteAsync(materialId.Trim().ToLowerInvariant(), User.GetUserId(), cancellationToken) ? NoContent() : throw ApiProblems.NotFound("Material override");
}
