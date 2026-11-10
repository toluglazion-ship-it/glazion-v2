using Glazion.Api.Infrastructure;
using Glazion.Api.Contracts;
using Glazion.Api.Data;
using Glazion.Api.Retrieval;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Glazion.Api.Controllers;

/// <summary>
/// Recipe-to-image retrieval (PRD §10 "Retrieval"; plan §10). New routes use the /api/v2 prefix and RFC 7807 problem
/// details with a stable machine-readable "code", as the specification requires for phase-two endpoints.
/// </summary>
[ApiController]
[Route("api/v2/recipe-image")]
[Authorize]
public sealed class RecipeImageController(RecipeImageSearchService search, IOptions<FeatureOptions> features) : ControllerBase
{
    [HttpPost("search")]
    public async Task<ActionResult<RecipeImageSearchResponse>> Search(RecipeImageSearchRequest request, [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey, CancellationToken cancellationToken)
    {
        if (Disabled()) return FeatureDisabled();
        // Searching by oxide values is for admins: users search with a recipe and see matches, not chemistry (SearchVisibility).
        if (!User.IsAdmin() && string.Equals(request.Query?.Type?.Trim(), "umf", StringComparison.OrdinalIgnoreCase))
            return ToProblem(new ApiProblemException(403, "umf_search_not_available", "Search by UMF isn't available", "Search with your recipe's materials instead."));
        // A regular user sees the matches, not how they were worked out (SearchVisibility).
        try { return Ok(SearchVisibility.ForViewer(await search.SearchAsync(request, Requester(), idempotencyKey, cancellationToken), User.IsAdmin())); }
        catch (ApiProblemException ex) { return ToProblem(ex); }
    }

    [HttpGet("search/{searchId:guid}")]
    public async Task<ActionResult<RecipeImageSearchResponse>> Get(Guid searchId, CancellationToken cancellationToken)
    {
        if (Disabled()) return FeatureDisabled();
        var result = await search.GetAsync(searchId, Requester(), cancellationToken);
        return result is null ? ToProblem(new ApiProblemException(404, "search_not_found", "Search not found", "No search with this ID is visible to you."))
            : Ok(SearchVisibility.ForViewer(result, User.IsAdmin()));
    }

    [HttpPost("feedback")]
    public async Task<ActionResult<SearchFeedbackResponse>> Feedback(SearchFeedbackRequest request, CancellationToken cancellationToken)
    {
        if (Disabled()) return FeatureDisabled();
        try { return Ok(await search.RecordFeedbackAsync(request, Requester(), cancellationToken)); }
        catch (ApiProblemException ex) { return ToProblem(ex); }
    }

    private SearchRequester Requester() => new(User.GetUserId(), User.IsAdmin());

    private bool Disabled() => !features.Value.RecipeImageRetrieval;

    private ObjectResult FeatureDisabled() => ToProblem(new ApiProblemException(403, "feature_disabled", "Recipe-to-image search is not enabled", "Recipe-to-image search is switched off at the moment."));

    private ObjectResult ToProblem(ApiProblemException ex) => ApiProblems.ToResult(HttpContext, ex);
}

/// <summary>Whether search is strict about firing (SearchSettings), so the search form can say so instead of offering the choice.</summary>
[ApiController]
[Route("api/v2/recipe-image/settings")]
[Authorize]
public sealed class SearchSettingsController(GlazionDbContext db) : ControllerBase
{
    [HttpGet]
    public Task<SearchSettingsDto> Get(CancellationToken cancellationToken) => SearchSettings.GetAsync(db, cancellationToken);
}

