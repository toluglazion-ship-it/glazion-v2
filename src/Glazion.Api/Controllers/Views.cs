using System.Text.Json;
using Glazion.Api.Contracts;
using Glazion.Api.Data;
using Glazion.Api.Domain;
using Glazion.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Glazion.Api.Controllers;

/// <summary>Entity → v2 DTO mapping shared by the core controllers.</summary>
public static class Views
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static UserDto User(AppUser u) => new(u.Id, u.Username, NullIfEmpty(u.DisplayName), u.Role, u.Status, u.CreatedAt, u.UpdatedAt, u.Email, u.ParticipantCode);

    public static CurrentUserDto CurrentUser(AppUser u) => new(u.Id, u.Username, NullIfEmpty(u.DisplayName), u.Role, u.Status, u.CreatedAt, u.Email, u.ParticipantCode);

    public static RecipeDto Recipe(Recipe x, string? baseCode) => new(x.Id, x.Name, x.Description, IsEmptyJson(x.RecipeJson) ? null : x.RecipeJson, x.IsActive, x.CreatedAt, x.UpdatedAt,
        x.Code, baseCode, x.RecipeNumber, x.AdditionsJson, x.OutcomeNote, Cone: x.Cone, Atmosphere: x.Atmosphere, FiringCone: x.FiringCone, FiringAtmosphere: x.FiringAtmosphere,
        DeletedAt: x.DeletedAt, MixChangedAt: x.MixChangedAt);

    public static RecipeDto Recipe(Recipe x) => Recipe(x, x.BaseGlaze?.Code);

    public static CaptureCodeDto Code(CaptureCode c, string? assignedUsername, int recipeCount, string? plainCode = null) =>
        new(c.Id, plainCode, c.MaskedSuffix, c.AssignedUserId, assignedUsername, c.LabelPrefix, IsUsable(c, DateTimeOffset.UtcNow), c.CreatedAt, c.ExpiresAt, c.RevokedAt, recipeCount);

    /// <summary>A code accepts uploads while it is active, not revoked, and not expired.</summary>
    public static bool IsUsable(CaptureCode c, DateTimeOffset now) => c.IsActive && c.RevokedAt == null && (c.ExpiresAt == null || c.ExpiresAt > now);

    public static IQueryable<CaptureCode> Usable(this IQueryable<CaptureCode> codes, DateTimeOffset now) =>
        codes.Where(c => c.IsActive && c.RevokedAt == null && (c.ExpiresAt == null || c.ExpiresAt > now));

    public static async Task<BaseGlazeResponse> BaseAsync(GlazionDbContext db, Guid baseId, bool includeRecipes, CancellationToken cancellationToken)
    {
        var b = await db.BaseGlazes.AsNoTracking().Include(x => x.DerivedFromBase).Include(x => x.PromotedFromRecipe).SingleAsync(x => x.Id == baseId, cancellationToken);
        var all = db.Recipes.AsNoTracking().Where(x => x.BaseGlazeId == baseId);
        // Deleted variations keep their numbers (max) but leave the list; a deleted base lists those deleted with it.
        var recipesQuery = all.Where(x => x.DeletedAt == b.DeletedAt);
        var count = await recipesQuery.CountAsync(cancellationToken);
        var max = await all.MaxAsync(x => x.RecipeNumber, cancellationToken) ?? 0;
        var recipes = includeRecipes ? (await recipesQuery.OrderBy(x => x.RecipeNumber).ToListAsync(cancellationToken)).Select(r => { r.BaseGlaze = b; return Recipe(r, b.Code); }).ToList() : null;
        var formulation = JsonSerializer.Deserialize<List<MaterialRow>>(b.FormulationJson, Json) ?? [];
        // One base at a time only: whether the formulation can still be corrected (none of its variations in use yet), and why not.
        var locks = includeRecipes ? await LabCodeService.FormulationLocksAsync(db, baseId, cancellationToken) : null;
        return new BaseGlazeResponse(b.Id, b.Code, b.ParticipantCode, b.BaseNumber, b.Name, b.Description, formulation, b.DerivedFromBase?.Code, b.ChangeNote, count, max >= LabCode.MaxRecipeNumber ? null : max + 1, b.CreatedAt, recipes, b.PromotedFromRecipe?.Code,
            b.UpdatedAt, locks is null ? null : locks.Count == 0, locks, b.Cone, b.Atmosphere, b.DeletedAt);
    }

    public static async Task<IReadOnlyList<BaseGlazeResponse>> BasesAsync(GlazionDbContext db, IEnumerable<Guid> ids, CancellationToken cancellationToken)
    {
        var result = new List<BaseGlazeResponse>();
        foreach (var id in ids) result.Add(await BaseAsync(db, id, includeRecipes: false, cancellationToken));
        return result;
    }

    private static bool IsEmptyJson(string? json) => string.IsNullOrWhiteSpace(json) || json.Trim() is "{}" or "null";
    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
