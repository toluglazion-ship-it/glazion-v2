using Glazion.Api.Contracts;
using Glazion.Api.Data;
using Glazion.Api.Infrastructure;
using Glazion.Api.Retrieval;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Glazion.Api.Controllers;

/// <summary>
/// Match check: every search anyone ran, with what they typed beside each tile it suggested, and where each tile came
/// from (file, batch or participant, its recipe and UMF). Admins only: none of this is shown to the public.
/// </summary>
[ApiController]
[Route("api/v2/admin/searches")]
[Authorize(Roles = "Admin")]
public sealed class AdminSearchesController(MatchCheckService matchCheck) : ControllerBase
{
    /// <summary>Newest first. type: raw_recipe, recipe or umf. notRelevant=true: only searches where a result was marked not relevant. q matches the person.</summary>
    [HttpGet]
    public Task<PagedResult<SearchCheckSummary>> List([FromQuery] Guid? userId, [FromQuery] string? type, [FromQuery] bool? notRelevant, [FromQuery] string? q,
        [FromQuery] string? cursor, [FromQuery] int? pageSize, CancellationToken cancellationToken) =>
        matchCheck.ListAsync(userId, type, notRelevant == true, q, cursor, pageSize, cancellationToken);

    [HttpGet("{searchId:guid}")]
    public async Task<ActionResult<SearchCheckDetail>> Get(Guid searchId, CancellationToken cancellationToken) =>
        await matchCheck.GetAsync(searchId, cancellationToken) is { } detail ? Ok(detail)
            : ApiProblems.ToResult(HttpContext, new ApiProblemException(404, "search_not_found", "Search not found", "No search with this ID."));
}

/// <summary>Search settings (SearchSettings): whether every search filters strictly by firing. Admins only.</summary>
[ApiController]
[Route("api/v2/admin/settings/search")]
[Authorize(Roles = "Admin")]
public sealed class AdminSearchSettingsController(GlazionDbContext db) : ControllerBase
{
    [HttpGet]
    public Task<SearchSettingsDto> Get(CancellationToken cancellationToken) => SearchSettings.GetAsync(db, cancellationToken);

    [HttpPut]
    public Task<SearchSettingsDto> Update(UpdateSearchSettingsRequest request, CancellationToken cancellationToken) =>
        SearchSettings.UpdateAsync(db, request, User.GetUserId(), cancellationToken);
}
