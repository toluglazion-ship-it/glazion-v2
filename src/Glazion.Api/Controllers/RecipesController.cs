using Glazion.Api.Infrastructure;
using Glazion.Api.Contracts;
using Glazion.Api.Retrieval;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Glazion.Api.Controllers;

/// <summary>
/// The recipe section opened from a search result: every active recipe and every base, read only, for any signed-in
/// account. Recipes are system scoped; editing stays on the admin and /me routes.
/// </summary>
[ApiController]
[Route("api/v2")]
[Authorize]
public sealed class RecipesController(RecipeLibraryService library) : ControllerBase
{
    [HttpGet("recipes")]
    public Task<PagedResult<RecipeSummaryDto>> Recipes([FromQuery] string? search, [FromQuery] string? baseCode, [FromQuery] string? cursor, [FromQuery] int? pageSize, CancellationToken cancellationToken) =>
        library.ListRecipesAsync(search, baseCode, cursor, pageSize, cancellationToken);

    /// <summary>The recipe with its materials, base and photos. Its chemistry (UMF) only for admins.</summary>
    [HttpGet("recipes/{recipeId:guid}")]
    public async Task<RecipeDetailDto> Recipe(Guid recipeId, CancellationToken cancellationToken) => ForViewer(await library.GetRecipeAsync(recipeId, cancellationToken));

    [HttpGet("recipes/by-code/{code}")]
    public async Task<RecipeDetailDto> RecipeByCode(string code, CancellationToken cancellationToken) => ForViewer(await library.GetRecipeByCodeAsync(code, cancellationToken));

    private RecipeDetailDto ForViewer(RecipeDetailDto recipe) => User.IsAdmin() ? recipe : recipe with { Chemistry = null };

    [HttpGet("base-glazes")]
    public Task<PagedResult<BaseGlazeSummaryDto>> Bases([FromQuery] string? search, [FromQuery] string? participantCode, [FromQuery] string? cursor, [FromQuery] int? pageSize, CancellationToken cancellationToken) =>
        library.ListBasesAsync(search, participantCode, cursor, pageSize, cancellationToken);

    [HttpGet("base-glazes/{code}")]
    public Task<BaseGlazeSummaryDto> Base(string code, CancellationToken cancellationToken) => library.GetBaseAsync(code, cancellationToken);

    /// <summary>The base's active variations, the same list as GET /recipes?baseCode={code}.</summary>
    [HttpGet("base-glazes/{code}/recipes")]
    public async Task<PagedResult<RecipeSummaryDto>> BaseRecipes(string code, [FromQuery] string? search, [FromQuery] string? cursor, [FromQuery] int? pageSize, CancellationToken cancellationToken)
    {
        await library.GetBaseAsync(code, cancellationToken); // 404 for an unknown base rather than an empty page
        return await library.ListRecipesAsync(search, code, cursor, pageSize, cancellationToken);
    }
}
