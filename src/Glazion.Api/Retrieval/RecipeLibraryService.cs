using System.Text.Json;
using Glazion.Api.Contracts;
using Glazion.Api.Data;
using Glazion.Api.Domain;
using Glazion.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Glazion.Api.Retrieval;

// The recipe section the apps open from a search result: any active recipe and any base, read only. Recipes are
// system scoped (like search itself), so every signed-in account sees the same library.

/// <summary>A recipe in a list. coverThumbnailUrl is its first approved tile photo, null when it has none yet.</summary>
public sealed record RecipeSummaryDto(Guid Id, string? Code, string Name, string Description, string? BaseCode, int? RecipeNumber,
    IReadOnlyList<MaterialRow> Additions, string? OutcomeNote, int PhotoCount, Uri? CoverThumbnailUrl);

/// <summary>
/// One recipe. materials is the complete recipe (base + additions for a lab-coded variation); base is the base it builds
/// on (null for a recipe outside the register); chemistry is its latest stored UMF, null until it has been calculated.
/// cone and atmosphere: how its tiles are fired, its own or its base's.
/// </summary>
public sealed record RecipeDetailDto(Guid Id, string? Code, string Name, string Description, int? RecipeNumber,
    IReadOnlyList<MaterialRow> Materials, IReadOnlyList<MaterialRow> Additions, string? OutcomeNote,
    BaseGlazeSummaryDto? Base, RecipeChemistryDto? Chemistry, IReadOnlyList<RecipePhotoDto> Photos, DateTimeOffset CreatedAt,
    string? Cone = null, string? Atmosphere = null);

public sealed record RecipeChemistryDto(IReadOnlyDictionary<string, decimal> Umf, string CalculationProfile, string CalculationVersion, DateTimeOffset CalculatedAt);

/// <summary>backUrl: the back of the tile (its code), when it was photographed; shown beside the photo, never searched.</summary>
public sealed record RecipePhotoDto(Guid ImageId, Uri DisplayUrl, Uri ThumbnailUrl, string TileVariant, decimal? ConeMin, decimal? ConeMax,
    string? Atmosphere, string? ClayBody, bool Verified, Uri? BackUrl = null);

/// <summary>variationCount counts active recipes only; list them with GET /api/v2/recipes?baseCode={code}.</summary>
public sealed record BaseGlazeSummaryDto(Guid Id, string Code, string Name, string Description, string ParticipantCode, int BaseNumber,
    IReadOnlyList<MaterialRow> Formulation, int VariationCount, string? DerivedFromBaseCode, string? ChangeNote, string? PromotedFromRecipeCode);

public sealed class RecipeLibraryService(GlazionDbContext db, IR2ObjectStorage storage)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Active recipes by lab code (uncoded ones last, by name). search matches name or code; baseCode lists one base's variations.</summary>
    public Task<PagedResult<RecipeSummaryDto>> ListRecipesAsync(string? search, string? baseCode, string? cursor, int? pageSize, CancellationToken cancellationToken)
    {
        var query = db.Recipes.AsNoTracking().Include(x => x.BaseGlaze).Where(x => x.IsActive);
        if (!string.IsNullOrWhiteSpace(baseCode))
        {
            var normalized = NormalizeBase(baseCode) ?? throw ApiProblems.Validation("baseCode", "A base code looks like P01-B1.");
            query = query.Where(x => x.BaseGlaze != null && x.BaseGlaze.Code == normalized);
        }
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLower();
            query = query.Where(x => x.Name.ToLower().Contains(term) || (x.Code != null && x.Code.ToLower().Contains(term)));
        }
        var ordered = query.OrderBy(x => x.Code == null).ThenBy(x => x.Code).ThenBy(x => x.Name).ThenBy(x => x.Id);
        return Paging.PageAsync(ordered, cursor, pageSize, SummariesAsync, cancellationToken);
    }

    public async Task<RecipeDetailDto> GetRecipeAsync(Guid id, CancellationToken cancellationToken) =>
        await DetailAsync(await db.Recipes.AsNoTracking().Include(x => x.BaseGlaze).SingleOrDefaultAsync(x => x.Id == id && x.IsActive, cancellationToken)
            ?? throw ApiProblems.NotFound("Recipe"), cancellationToken);

    public async Task<RecipeDetailDto> GetRecipeByCodeAsync(string code, CancellationToken cancellationToken)
    {
        var normalized = LabCode.NormalizeRecipe(code);
        var recipe = normalized is null ? null
            : await db.Recipes.AsNoTracking().Include(x => x.BaseGlaze).SingleOrDefaultAsync(x => x.Code == normalized && x.IsActive, cancellationToken);
        return await DetailAsync(recipe ?? throw new ApiProblemException(404, "recipe_code_not_found", "Not found", $"Recipe code '{code}' was not found."), cancellationToken);
    }

    /// <summary>Bases by participant then number. search matches name or code.</summary>
    public Task<PagedResult<BaseGlazeSummaryDto>> ListBasesAsync(string? search, string? participantCode, string? cursor, int? pageSize, CancellationToken cancellationToken)
    {
        var query = db.BaseGlazes.AsNoTracking().Include(x => x.DerivedFromBase).Include(x => x.PromotedFromRecipe).Where(x => x.DeletedAt == null);
        if (!string.IsNullOrWhiteSpace(participantCode)) query = query.Where(x => x.ParticipantCode == LabCode.NormalizeParticipant(participantCode));
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLower();
            query = query.Where(x => x.Name.ToLower().Contains(term) || x.Code.ToLower().Contains(term));
        }
        var ordered = query.OrderBy(x => x.ParticipantCode).ThenBy(x => x.BaseNumber).ThenBy(x => x.Id);
        return Paging.PageAsync(ordered, cursor, pageSize, BaseSummariesAsync, cancellationToken);
    }

    public async Task<BaseGlazeSummaryDto> GetBaseAsync(string code, CancellationToken cancellationToken)
    {
        var normalized = NormalizeBase(code);
        var entity = normalized is null ? null
            : await db.BaseGlazes.AsNoTracking().Include(x => x.DerivedFromBase).Include(x => x.PromotedFromRecipe).SingleOrDefaultAsync(x => x.Code == normalized && x.DeletedAt == null, cancellationToken);
        return (await BaseSummariesAsync([entity ?? throw new ApiProblemException(404, "base_not_found", "Not found", $"Base {code} does not exist.")]))[0];
    }

    // ---- views ----

    private async Task<RecipeDetailDto> DetailAsync(Recipe recipe, CancellationToken cancellationToken)
    {
        var baseSummary = recipe.BaseGlaze is null ? null : (await BaseSummariesAsync([recipe.BaseGlaze]))[0];
        var snapshot = await db.RecipeCalculationResults.AsNoTracking().Where(x => x.RecipeId == recipe.Id).OrderByDescending(x => x.CreatedAt).FirstOrDefaultAsync(cancellationToken);
        var chemistry = snapshot is null ? null : new RecipeChemistryDto(
            JsonSerializer.Deserialize<Dictionary<string, decimal>>(snapshot.UmfJson, Json) ?? [], snapshot.CalculationProfile, snapshot.CalculationVersion, snapshot.CreatedAt);

        var links = await VisiblePhotos(db.RecipeImages.Where(x => x.RecipeId == recipe.Id))
            .Include(x => x.Image).OrderByDescending(x => x.Verified).ThenBy(x => x.CreatedAt).ToListAsync(cancellationToken);
        var photos = new List<RecipePhotoDto>(links.Count);
        foreach (var link in links)
        {
            var (display, thumbnail) = await TilePhotoUrls.SignAsync(storage, link.Image, recipe.Code, cancellationToken);
            photos.Add(new RecipePhotoDto(link.ImageId, display, thumbnail, link.Image.TileVariantKey, link.Image.ConeMin, link.Image.ConeMax,
                link.Image.Atmosphere, link.Image.ClayBody, link.Verified, await TilePhotoUrls.BackAsync(storage, link.Image, recipe.Code, cancellationToken)));
        }

        var additions = Rows(recipe.AdditionsJson);
        var materials = MaterialsOf(recipe);
        if (materials.Count == 0 && recipe.BaseGlaze is not null) materials = LabCodeService.Combine(Rows(recipe.BaseGlaze.FormulationJson), additions);
        return new RecipeDetailDto(recipe.Id, recipe.Code, recipe.Name, recipe.Description, recipe.RecipeNumber, materials, additions, recipe.OutcomeNote,
            baseSummary, chemistry, photos, recipe.CreatedAt, recipe.FiringCone, recipe.FiringAtmosphere);
    }

    private async Task<IReadOnlyList<RecipeSummaryDto>> SummariesAsync(List<Recipe> recipes)
    {
        var ids = recipes.Select(x => x.Id).ToList();
        var links = await VisiblePhotos(db.RecipeImages.Where(x => ids.Contains(x.RecipeId))).Include(x => x.Image).ToListAsync();
        var byRecipe = links.GroupBy(x => x.RecipeId).ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.Verified).ThenBy(x => x.CreatedAt).ToList());
        var result = new List<RecipeSummaryDto>(recipes.Count);
        foreach (var r in recipes)
        {
            Uri? cover = null;
            var photos = byRecipe.GetValueOrDefault(r.Id) ?? [];
            if (photos.Count > 0) cover = (await TilePhotoUrls.SignAsync(storage, photos[0].Image, r.Code, CancellationToken.None)).Thumbnail;
            result.Add(new RecipeSummaryDto(r.Id, r.Code, r.Name, r.Description, r.BaseGlaze?.Code, r.RecipeNumber, Rows(r.AdditionsJson), r.OutcomeNote, photos.Count, cover));
        }
        return result;
    }

    private async Task<IReadOnlyList<BaseGlazeSummaryDto>> BaseSummariesAsync(List<BaseGlaze> bases)
    {
        var ids = bases.Select(x => x.Id).ToList();
        var counts = await db.Recipes.AsNoTracking().Where(x => x.IsActive && x.BaseGlazeId != null && ids.Contains(x.BaseGlazeId.Value))
            .GroupBy(x => x.BaseGlazeId!.Value).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count);
        return bases.Select(b => new BaseGlazeSummaryDto(b.Id, b.Code, b.Name, b.Description, b.ParticipantCode, b.BaseNumber, Rows(b.FormulationJson),
            counts.GetValueOrDefault(b.Id), b.DerivedFromBase?.Code, b.ChangeNote, b.PromotedFromRecipe?.Code)).ToList();
    }

    /// <summary>The same photos search may return: approved and not failed on quality.</summary>
    private static IQueryable<RecipeImage> VisiblePhotos(IQueryable<RecipeImage> links) =>
        links.AsNoTracking().Where(x => x.Image.ApprovalStatus == ApprovalStatus.Approved && x.Image.QualityStatus != QualityStatus.Failed);

    // ---- material rows ----

    private static string? NormalizeBase(string? code) => LabCode.TryParseBase(code, out var participant, out var number) ? LabCode.Base(participant, number) : null;

    /// <summary>The complete rows a recipe stores: "full" for lab-coded variations, otherwise "materials" or "rows" (as the calculator reads them).</summary>
    private static IReadOnlyList<MaterialRow> MaterialsOf(Recipe recipe)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(recipe.RecipeJson) ? "{}" : recipe.RecipeJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return [];
            foreach (var key in new[] { "full", "materials", "rows" })
                if (root.TryGetProperty(key, out var array) && array.ValueKind == JsonValueKind.Array && array.GetArrayLength() > 0)
                    return ParseRows(array);
        }
        catch (JsonException) { }
        return [];
    }

    private static IReadOnlyList<MaterialRow> Rows(string? json)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "[]" : json);
            return doc.RootElement.ValueKind == JsonValueKind.Array ? ParseRows(doc.RootElement) : [];
        }
        catch (JsonException) { return []; }
    }

    /// <summary>Rows named by rawName, material or name; an amount stored as a number or a numeric string. Unreadable rows are skipped.</summary>
    private static List<MaterialRow> ParseRows(JsonElement array)
    {
        var rows = new List<MaterialRow>();
        foreach (var e in array.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Object) continue;
            string? name = null;
            foreach (var key in new[] { "rawName", "material", "name" })
                if (e.TryGetProperty(key, out var n) && n.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(n.GetString())) { name = n.GetString()!.Trim(); break; }
            if (name is null || !e.TryGetProperty("amount", out var a)) continue;
            decimal amount;
            if (a.ValueKind == JsonValueKind.Number) amount = a.GetDecimal();
            else if (a.ValueKind != JsonValueKind.String || !decimal.TryParse(a.GetString(), System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out amount)) continue;
            var unit = e.TryGetProperty("unit", out var u) && u.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(u.GetString()) ? u.GetString()!.Trim() : "%";
            rows.Add(new MaterialRow(name, amount, unit));
        }
        return rows;
    }
}

/// <summary>Signed photo URLs, named by the lab code so a saved file is recognisable. Derivatives are used once they exist.</summary>
public static class TilePhotoUrls
{
    public static async Task<(Uri Display, Uri Thumbnail)> SignAsync(IR2ObjectStorage storage, ImageAsset asset, string? code, CancellationToken cancellationToken)
    {
        var name = code ?? asset.Id.ToString("D");
        var ext = ObjectKeyPolicy.ExtensionFor(asset.DeclaredContentType);
        var display = await storage.CreateGetUrlAsync(asset.DisplayKey ?? asset.OriginalKey, $"{name}.{(asset.DisplayKey is null ? ext : "webp")}", cancellationToken);
        var thumbnail = await storage.CreateGetUrlAsync(asset.ThumbnailKey ?? asset.OriginalKey, $"{name}-thumb.{(asset.ThumbnailKey is null ? ext : "webp")}", cancellationToken);
        return (display, thumbnail);
    }

    public static async Task<Uri?> BackAsync(IR2ObjectStorage storage, ImageAsset asset, string? code, CancellationToken cancellationToken) =>
        asset.BackKey is { } key
            ? await storage.CreateGetUrlAsync(key, $"{code ?? asset.Id.ToString("D")}-back.{ObjectKeyPolicy.ExtensionFor(asset.BackContentType ?? asset.DeclaredContentType)}", cancellationToken)
            : null;
}
