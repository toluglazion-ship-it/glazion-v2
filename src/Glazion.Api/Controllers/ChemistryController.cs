using Glazion.Api.Chemistry;
using Glazion.Api.Contracts;
using Glazion.Api.Data;
using Glazion.Api.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Glazion.Api.Controllers;

/// <summary>UMF calculator and materials (PRD §10 "Recipes/UMF"). The apps call this instead of computing UMF locally.</summary>
[ApiController]
[Route("api/v2")]
[Authorize]
public sealed class ChemistryController(UmfCalculationService umf, GlazionDbContext db) : ControllerBase
{
    /// <summary>
    /// Stateless calculation; same rows in, same chem out as the client's calculateRecipe. Nothing is stored. Open to every
    /// user: it only works out the chemistry of the recipe they type, not of Glazion's stored recipes.
    /// </summary>
    [HttpPost("umf/calculate")]
    public ActionResult<UmfCalculateResponse> Calculate(UmfCalculateRequest request)
    {
        try { return Ok(umf.Calculate(request)); }
        catch (ApiProblemException ex) { return ApiProblems.ToResult(HttpContext, ex); }
    }

    /// <summary>Target UMF ranges (glaze-limits.json) for a cone, or every range without one. Only cones 06, 6, 9 and 10 have ranges.</summary>
    [HttpGet("umf/limits")]
    [HttpGet("glaze-limits")]
    public ActionResult<IReadOnlyList<GlazeLimit>> Limits([FromQuery] string? cone)
    {
        try { return Ok(GlazeChemistry.LimitsForCone(umf.RequireData(), cone)); }
        catch (ApiProblemException ex) { return ApiProblems.ToResult(HttpContext, ex); }
    }

    /// <summary>
    /// The materials dropdown. q searches names and aliases (ranked, one typo allowed); category, common=true and
    /// withAnalysis=true narrow the list. With no q, common=true returns the studio short list grouped by category.
    /// Pages with cursor/pageSize (follow nextCursor) or offset/limit; at most 500 per page.
    /// </summary>
    [HttpGet("materials")]
    public ActionResult<MaterialListResponse> Materials([FromQuery] string? q, [FromQuery] int limit = 50, [FromQuery] int offset = 0,
        [FromQuery] string? category = null, [FromQuery] bool common = false, [FromQuery] bool withAnalysis = false,
        [FromQuery] string? cursor = null, [FromQuery] int? pageSize = null)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(cursor)) offset = Paging.Decode(cursor);
            if (pageSize is { } size) limit = size;
            return Ok(umf.ListMaterials(q, limit, offset, category, common, withAnalysis));
        }
        catch (ApiProblemException ex) { return ApiProblems.ToResult(HttpContext, ex); }
    }

    /// <summary>Calculate a stored recipe and keep the snapshot (UMF-01 "save"). 201 for a new snapshot, 200 when the same inputs were already calculated.</summary>
    [HttpPost("recipes/{recipeId:guid}/calculate")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<RecipeCalculationResponse>> CalculateRecipe(Guid recipeId, CancellationToken cancellationToken)
    {
        var recipe = await db.Recipes.SingleOrDefaultAsync(x => x.Id == recipeId && x.IsActive, cancellationToken);
        return await CalculateAsync(recipe, cancellationToken);
    }

    [HttpPost("recipes/by-code/{code}/calculate")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<RecipeCalculationResponse>> CalculateRecipeByCode(string code, CancellationToken cancellationToken)
    {
        var normalized = LabCode.NormalizeRecipe(code);
        var recipe = normalized is null ? null : await db.Recipes.SingleOrDefaultAsync(x => x.Code == normalized && x.IsActive, cancellationToken);
        return await CalculateAsync(recipe, cancellationToken);
    }

    private async Task<ActionResult<RecipeCalculationResponse>> CalculateAsync(Domain.Recipe? recipe, CancellationToken cancellationToken)
    {
        try
        {
            if (recipe is null) throw new ApiProblemException(404, "recipe_not_found", "Recipe not found", "The recipe does not exist.");
            var result = await umf.CalculateRecipeAsync(recipe, User.GetUserId(), User.IsAdmin(), cancellationToken);
            return result.Created ? StatusCode(201, result) : Ok(result);
        }
        catch (ApiProblemException ex) { return ApiProblems.ToResult(HttpContext, ex); }
    }
}
