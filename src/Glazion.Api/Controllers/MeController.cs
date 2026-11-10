using Glazion.Api.Contracts;
using Glazion.Api.Data;
using Glazion.Api.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Glazion.Api.Controllers;

[ApiController]
[Route("api/v2/me")]
// No class-level [Authorize]: attributes combine, and /me must stay reachable for Pending accounts.
public sealed class MeController(GlazionDbContext db, LabCodeService labCodes) : ControllerBase
{
    /// <summary>The signed-in account. Reachable while Pending so the apps can tell the user they are waiting for approval.</summary>
    [HttpGet]
    [Authorize(Policy = GlazionPolicies.AnyAccount)]
    public async Task<ActionResult<CurrentUserDto>> Get(CancellationToken cancellationToken)
    {
        var user = await db.Users.AsNoTracking().SingleAsync(x => x.Id == User.GetUserId(), cancellationToken);
        return Ok(Views.CurrentUser(user));
    }

    /// <summary>The newest usable capture code assigned to the caller and its recipes. 404 when there is none.</summary>
    [HttpGet("capture-context")]
    [Authorize]
    public async Task<ActionResult<CaptureContextDto>> CaptureContext(CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();
        var code = await db.CaptureCodes.AsNoTracking().Where(x => x.AssignedUserId == userId).Usable(DateTimeOffset.UtcNow)
            .OrderByDescending(x => x.CreatedAt).FirstOrDefaultAsync(cancellationToken);
        if (code is null) throw new ApiProblemException(404, "no_capture_code", "No capture code", "No active capture code is assigned to you yet. Ask an administrator for one.");
        var recipes = await db.Recipes.AsNoTracking().Include(x => x.BaseGlaze).Where(x => x.IsActive && (x.BaseGlaze == null || x.BaseGlaze.DeletedAt == null) && x.Codes.Any(c => c.CaptureCodeId == code.Id))
            .OrderBy(x => x.Name).ToListAsync(cancellationToken);
        return Ok(new CaptureContextDto(code.Id, code.MaskedSuffix, code.LabelPrefix, code.ExpiresAt, recipes.Select(Views.Recipe).ToList()));
    }

    /// <summary>Recipes assigned to the caller through any usable capture code, plus the caller's own coded variations; none of a deleted base.</summary>
    [HttpGet("recipes")]
    [Authorize]
    public Task<PagedResult<RecipeDto>> Recipes([FromQuery] string? cursor, [FromQuery] int? pageSize, CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();
        var now = DateTimeOffset.UtcNow;
        var assigned = db.CodeRecipes.Where(x => x.CaptureCode.AssignedUserId == userId && x.CaptureCode.IsActive && x.CaptureCode.RevokedAt == null && (x.CaptureCode.ExpiresAt == null || x.CaptureCode.ExpiresAt > now)).Select(x => x.RecipeId);
        var ordered = db.Recipes.AsNoTracking().Include(x => x.BaseGlaze)
            .Where(x => x.IsActive && (x.BaseGlaze == null || x.BaseGlaze.DeletedAt == null) && (assigned.Contains(x.Id) || x.OwnerUserId == userId)).OrderBy(x => x.Name).ThenBy(x => x.Id);
        return Paging.PageAsync(ordered, cursor, pageSize, rows => Task.FromResult<IReadOnlyList<RecipeDto>>(rows.Select(Views.Recipe).ToList()), cancellationToken);
    }

    // ---- The caller's base-glaze register (docs/features/lab-code-naming.md) ----

    [HttpGet("base-glazes")]
    [Authorize]
    public Task<PagedResult<BaseGlazeResponse>> MyBases([FromQuery] string? cursor, [FromQuery] int? pageSize, CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();
        var ordered = db.BaseGlazes.AsNoTracking().Where(x => x.OwnerUserId == userId && x.DeletedAt == null).OrderBy(x => x.BaseNumber).Select(x => x.Id);
        return Paging.PageAsync(ordered, cursor, pageSize, ids => Views.BasesAsync(db, ids, cancellationToken), cancellationToken);
    }

    [HttpPost("base-glazes")]
    [Authorize]
    public async Task<ActionResult<BaseGlazeResponse>> RegisterMyBase(RegisterBaseGlazeRequest request, CancellationToken cancellationToken)
    {
        var owner = await db.Users.SingleAsync(x => x.Id == User.GetUserId(), cancellationToken);
        // Deriving from another participant's base is allowed; the new base is still the caller's own register entry.
        var entity = await labCodes.RegisterBaseAsync(owner, request.Name, request.Description, request.Formulation ?? [], request.DerivedFromBaseCode, request.ChangeNote, owner.Id, cancellationToken,
            new FiringInput(request.Cone, request.Atmosphere));
        return Created($"/api/v2/me/base-glazes/{entity.Code}", await Views.BaseAsync(db, entity.Id, includeRecipes: false, cancellationToken));
    }

    [HttpGet("base-glazes/{code}")]
    [Authorize]
    public async Task<ActionResult<BaseGlazeResponse>> MyBase(string code, CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();
        var id = await db.BaseGlazes.AsNoTracking().Where(x => x.Code == code.Trim().ToUpperInvariant() && x.OwnerUserId == userId && x.DeletedAt == null).Select(x => (Guid?)x.Id).SingleOrDefaultAsync(cancellationToken);
        if (id is null) throw new ApiProblemException(404, "base_not_found", "Not found", $"Base {code} is not in your register.");
        return Ok(await Views.BaseAsync(db, id.Value, includeRecipes: true, cancellationToken));
    }

    [HttpPost("base-glazes/{code}/recipes")]
    [Authorize]
    public async Task<ActionResult<RecipeDto>> RegisterMyVariation(string code, RegisterVariationRequest request, CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();
        var baseGlaze = await db.BaseGlazes.SingleOrDefaultAsync(x => x.Code == code.Trim().ToUpperInvariant() && x.OwnerUserId == userId, cancellationToken)
            ?? throw new ApiProblemException(404, "base_not_found", "Not found", $"Base {code} is not in your register.");
        var recipe = (await labCodes.RegisterVariationsAsync(baseGlaze, [new VariationInput(request.Name, request.Description, request.Additions ?? [], new FiringInput(request.Cone, request.Atmosphere))],
            userId, cancellationToken))[0];
        return Created($"/api/v2/me/recipes/by-code/{recipe.Code}", Views.Recipe(recipe, baseGlaze.Code));
    }

    [HttpPost("base-glazes/{code}/recipes/batch")]
    [Authorize]
    public async Task<ActionResult<IReadOnlyList<RecipeDto>>> RegisterMyVariations(string code, RegisterVariationsRequest request, CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();
        if (request.CaptureCodeId is not null) throw ApiProblems.Validation("captureCodeId", "Only an administrator assigns recipes to capture codes.");
        var baseGlaze = await db.BaseGlazes.SingleOrDefaultAsync(x => x.Code == code.Trim().ToUpperInvariant() && x.OwnerUserId == userId, cancellationToken)
            ?? throw new ApiProblemException(404, "base_not_found", "Not found", $"Base {code} is not in your register.");
        var recipes = await labCodes.RegisterVariationsAsync(baseGlaze, AdminController.ToInputs(request), userId, cancellationToken);
        return StatusCode(201, recipes.Select(r => Views.Recipe(r, baseGlaze.Code)).ToList());
    }

    [HttpGet("recipes/by-code/{code}")]
    [Authorize]
    public async Task<ActionResult<RecipeDto>> MyRecipeByCode(string code, CancellationToken cancellationToken) =>
        Ok(Views.Recipe(await FindPermittedAsync(code, cancellationToken)));

    /// <summary>
    /// Builds on a variation the caller owns or was assigned: its base + additions become a new base in the caller's
    /// register. 409 if the variation was already promoted.
    /// </summary>
    [HttpPost("recipes/by-code/{code}/promote")]
    [Authorize]
    public async Task<ActionResult<BaseGlazeResponse>> PromoteMyVariation(string code, [FromBody(EmptyBodyBehavior = Microsoft.AspNetCore.Mvc.ModelBinding.EmptyBodyBehavior.Allow)] PromoteVariationRequest? request, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(request?.ParticipantCode)) throw ApiProblems.Validation("participantCode", "A promoted base always goes into your own register.");
        var variation = await FindPermittedAsync(code, cancellationToken);
        var owner = await db.Users.SingleAsync(x => x.Id == User.GetUserId(), cancellationToken);
        var entity = await labCodes.PromoteVariationAsync(variation, owner, request?.Name, request?.Description, owner.Id, cancellationToken);
        return Created($"/api/v2/me/base-glazes/{entity.Code}", await Views.BaseAsync(db, entity.Id, includeRecipes: false, cancellationToken));
    }

    /// <summary>A recipe the caller owns, or one assigned to them through an active capture code; 404 otherwise.</summary>
    private async Task<Domain.Recipe> FindPermittedAsync(string code, CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();
        var recipe = await labCodes.FindByCodeAsync(code, cancellationToken);
        var permitted = recipe is not null && (recipe.OwnerUserId == userId ||
            await db.CodeRecipes.AnyAsync(x => x.RecipeId == recipe.Id && x.CaptureCode.AssignedUserId == userId && x.CaptureCode.IsActive && x.CaptureCode.RevokedAt == null, cancellationToken));
        if (!permitted) throw new ApiProblemException(404, "recipe_code_not_found", "Not found", $"Recipe code '{code}' was not found.");
        return recipe!;
    }
}
