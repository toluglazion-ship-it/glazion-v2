using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Glazion.Api.Contracts;
using Glazion.Api.Data;
using Glazion.Api.Domain;
using Glazion.Api.Indexing;
using Glazion.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Glazion.Api.Chemistry;

public sealed record UmfCalculateRequest(IReadOnlyList<RecipeRowInput>? Rows, string? Cone);

public sealed record UmfVersions(string CalculationProfile, string CalculationVersion, string OxidesVersion, string MaterialsVersion, string LimitsVersion);

public sealed record OxideValue(string Oxide, double Value);
public sealed record DisplayGroupResponse(string Key, string Label, IReadOnlyList<OxideValue> Oxides);

/// <summary>
/// The result as the apps' calculateRecipe returns it (handoff API.md), field for field. Ratios that can't be worked
/// out are left out of the JSON. Suggestions are empty without a cone, or for a cone that has no target ranges.
/// </summary>
public sealed record UmfResult(
    IReadOnlyList<RecipeLine> Lines,
    OrderedDictionary<string, double> Moles,
    OrderedDictionary<string, double> MolePct,
    OrderedDictionary<string, double> AnalysisPct,
    OrderedDictionary<string, double> Umf,
    double FluxSum,
    IReadOnlyList<string> Warnings,
    ChemRatios Ratios,
    double Cte,
    IReadOnlyList<GlazeChemistry.Suggestion> Suggestions,
    string ClayBodyNote);

/// <summary>index is the row's position in the request; material is additive (the name or id the row named).</summary>
public sealed record UmfSkippedRow(int Index, string Reason, string? Material);

/// <summary>
/// POST /api/v2/umf/calculate, as agreed with the frontend (handoff API.md): materialsVersion, skippedRows and result
/// (null when nothing could be calculated). Additive: the cone as normalised, that cone's target ranges, the UMF
/// display groups, the CTE band, and the calculation versions.
/// </summary>
public sealed record UmfCalculateResponse(
    string MaterialsVersion, IReadOnlyList<UmfSkippedRow> SkippedRows, UmfResult? Result,
    string? Cone, IReadOnlyList<GlazeLimit> Limits, IReadOnlyList<DisplayGroupResponse> DisplayGroups, string? CteBand, UmfVersions Versions);

/// <summary>
/// Same shape as Material in the client's types.ts (normaliseMaterials), plus, for the dropdown: hasAnalysis; source
/// ("file", "override" when an administrator corrected it, "custom" when an administrator added it); category;
/// common (on the studio short list); aliases (other names it goes by); matchedAlias (the alias the search text
/// matched, so "EPK" can show "EP Kaolin (EPK)").
/// </summary>
public sealed record MaterialSummary(
    string Id, string Name, string Type,
    [property: System.Text.Json.Serialization.JsonPropertyName("loi_pct")] double LoiPct,
    JsonElement OxideAnalysis, bool HasAnalysis, string Source = MaterialSources.File,
    string Category = "other", bool Common = false, IReadOnlyList<string>? Aliases = null, string? MatchedAlias = null);
/// <summary>categories lists every category key with its label, for filter chips and group headings.</summary>
/// <summary>nextCursor fetches the following page (null on the last one); offset/limit paging still works.</summary>
public sealed record MaterialListResponse(string MaterialsVersion, int Total, IReadOnlyList<MaterialSummary> Items, IReadOnlyList<MaterialCategory>? Categories = null, string? NextCursor = null);

public sealed record RowResolution(int Position, string? RawName, string Status, string? MaterialId, string? MaterialName, IReadOnlyList<string> Suggestions);

public sealed record RecipeCalculationResponse(Guid SnapshotId, Guid RecipeId, string? RecipeCode, bool Created, UmfVersions Versions, string InputHash, RecipeResult Result, IReadOnlyList<string> SchemaWarnings, int ImagesRepinned, DateTimeOffset CreatedAt);

/// <summary>
/// Backend home of the UMF calculator (PRD §5.5 UMF-01…05). Stateless calculation reproduces the client exactly;
/// persisting a recipe's chemistry is stricter, because a stored snapshot feeds indexing and search: every row must
/// resolve to a material with a usable amount (UMF-02), otherwise nothing is stored.
/// </summary>
public sealed class UmfCalculationService(GlazionDbContext db, ChemistryDataProvider provider, ILogger<UmfCalculationService> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public ChemistryData RequireData()
    {
        if (provider.TryGet(out var data, out var error)) return data;
        throw new ApiProblemException(503, "chemistry_data_unavailable", "UMF calculator unavailable", error ?? "Chemistry data is not loaded.");
    }

    public static UmfVersions Versions(ChemistryData data) => new(GlazeChemistry.CalculationProfile, GlazeChemistry.CalculationVersion, data.OxidesVersion, data.MaterialsVersion, data.LimitsVersion);

    /// <summary>Stateless calculation of a recipe that may not be saved: same rows in, same numbers out as the apps. No writes.</summary>
    public UmfCalculateResponse Calculate(UmfCalculateRequest request)
    {
        var data = RequireData();
        var rows = request.Rows ?? [];
        if (rows.Count > 200) throw Validation("rows", "At most 200 rows.");
        var skipped = new List<SkippedRow>();
        var calculated = GlazeChemistry.CalculateRecipe(rows, data, skipped);
        var cone = string.IsNullOrWhiteSpace(request.Cone) ? null : GlazeChemistry.NormaliseCone(request.Cone);
        UmfResult? result = null;
        if (calculated is { Chem: var chem })
            result = new UmfResult(calculated.Lines, chem.Moles, chem.MolePct, chem.AnalysisPct, chem.Umf, chem.FluxSum, chem.Warnings, chem.Ratios, chem.Cte,
                cone is null ? [] : GlazeChemistry.SuggestOptimisations(data, calculated, cone), GlazeChemistry.ClayBodyNote(chem.Cte));
        return new UmfCalculateResponse(data.MaterialsVersion, skipped.Select(s => new UmfSkippedRow(s.Index, s.Reason, s.Material)).ToList(), result,
            cone,
            cone is null ? [] : GlazeChemistry.LimitsForCone(data, cone),
            calculated is null ? [] : GlazeChemistry.DisplayGroups(calculated.Chem).Select(g => new DisplayGroupResponse(g.Key, g.Label, g.Oxides.Select(o => new OxideValue(o.Key, o.Value)).ToList())).ToList(),
            calculated is null ? null : GlazeChemistry.CteBand(calculated.Chem.Cte),
            Versions(data));
    }

    /// <summary>
    /// The dropdown's list (MaterialSearch): ranked by name and alias when q is given, one entry per material,
    /// optionally narrowed to a category, the common studio list, or materials with an analysis.
    /// </summary>
    public MaterialListResponse ListMaterials(string? query, int limit, int offset, string? category = null, bool common = false, bool withAnalysis = false)
    {
        var data = RequireData();
        var curation = provider.Curation;
        limit = Math.Clamp(limit, 1, 500);
        offset = Math.Max(0, offset);
        var categoryKey = string.IsNullOrWhiteSpace(category) ? null : category.Trim().ToLowerInvariant();
        if (categoryKey is not null && curation.Categories.All(c => c.Key != categoryKey))
            throw Validation("category", $"Unknown category. Use one of {string.Join(", ", curation.Categories.Select(c => c.Key))}.");
        if (query is { Length: > 100 }) throw Validation("q", "Search text must be at most 100 characters.");

        var hits = MaterialSearch.Search(data, curation, new MaterialQuery(query, categoryKey, common, withAnalysis));
        var items = hits.Skip(offset).Take(limit).Select(h => new MaterialSummary(h.Material.Id, h.Material.Name, "raw", h.Material.Loi ?? 0,
            JsonSerializer.SerializeToElement(h.Material.OxideAnalysis.ToDictionary(x => x.Key, x => x.Value)), h.Material.OxideAnalysis.Count > 0,
            data.SourceOf(h.Material.Id), h.Category, h.Common, h.Aliases, h.MatchedAlias)).ToList();
        var next = offset + items.Count < hits.Count ? Paging.Encode(offset + items.Count) : null;
        return new MaterialListResponse(data.MaterialsVersion, hits.Count, items, curation.Categories, next);
    }

    /// <summary>
    /// Strict resolution for anything that will be stored or searched: each row must name a material in the list
    /// and carry a finite amount. The stateless calculator skips such rows, as the client does; this refuses them.
    /// </summary>
    public static IReadOnlyList<RowResolution> Resolve(ChemistryData data, IReadOnlyList<RecipeRowInput> rows, MaterialCuration? curation = null)
    {
        var result = new List<RowResolution>(rows.Count);
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            // A dropdown id resolves exactly as the calculator resolves it: to that entry's name, then the name lookup.
            var byId = !string.IsNullOrEmpty(row.MaterialId);
            var name = byId ? (data.MaterialsById.TryGetValue(row.MaterialId!, out var picked) ? picked.Name : null) : row.Material;
            var raw = byId ? row.MaterialId : row.Material;
            var material = string.IsNullOrWhiteSpace(name) ? null : GlazeChemistry.FindByName(data, name);
            var amountOk = row.Amount is { } a && a.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined) && double.IsFinite(GlazeChemistry.ParseFloat(a));
            if (material is not null && amountOk)
            {
                // The arithmetic reads the analysis by id, where the later of two same-id entries wins. Report the
                // entry actually used so a duplicate-name surprise is visible.
                var used = data.MaterialsById[material.Id];
                result.Add(new RowResolution(i + 1, raw, "resolved", used.Id, used.Name, []));
            }
            else if (material is null) result.Add(new RowResolution(i + 1, raw, "unresolved", null, null, Suggest(data, row.Material, curation: curation)));
            else result.Add(new RowResolution(i + 1, raw, "invalid_amount", material.Id, material.Name, []));
        }
        return result;
    }

    /// <summary>
    /// Stored recipe rows name materials the way potters write them, so a row whose name is not a material's but is one
    /// of the catalog's aliases ("Shell Pink 6000", "RIO", "EPK") is pointed at that material by id. Rows that already
    /// carry an id, or match a name exactly, are left as they are. The calculator itself only matches exact names.
    /// </summary>
    public static IReadOnlyList<RecipeRowInput> WithAliases(IReadOnlyList<RecipeRowInput> rows, ChemistryData data, MaterialCuration curation) =>
        rows.Select(row =>
            string.IsNullOrEmpty(row.MaterialId) && !string.IsNullOrWhiteSpace(row.Material) && GlazeChemistry.FindByName(data, row.Material) is null
            && curation.AliasToId.TryGetValue(MaterialSearch.Normalize(row.Material), out var id) && data.MaterialsById.ContainsKey(id)
                ? row with { MaterialId = id }
                : row).ToList();

    /// <summary>Near names for an unresolved row. A known alias ("EPK", "Whiting") puts its material first.</summary>
    public static IReadOnlyList<string> Suggest(ChemistryData data, string? rawName, int max = 5, MaterialCuration? curation = null)
    {
        if (string.IsNullOrWhiteSpace(rawName)) return [];
        if (curation is not null && curation.AliasToId.TryGetValue(MaterialSearch.Normalize(rawName), out var aliasId) && data.MaterialsById.TryGetValue(aliasId, out var aliased))
            return new[] { aliased.Name }.Concat(SuggestByName(data, rawName, max)).Distinct(StringComparer.Ordinal).Take(max).ToList();
        return SuggestByName(data, rawName, max);
    }

    private static IReadOnlyList<string> SuggestByName(ChemistryData data, string rawName, int max)
    {
        var q = rawName.Trim().ToLowerInvariant();
        var tokens = q.Split([' ', '-', '_', '.', ',', '(', ')'], StringSplitOptions.RemoveEmptyEntries).Where(t => t.Length >= 3).ToArray();
        return data.Materials
            .Select(m => (m.Name, lower: m.Name.ToLowerInvariant()))
            .Select(x => (x.Name, score: x.lower.Contains(q) || q.Contains(x.lower) ? 2 : tokens.Length > 0 && tokens.All(t => x.lower.Contains(t)) ? 1 : 0))
            .Where(x => x.score > 0)
            .OrderByDescending(x => x.score).ThenBy(x => x.Name.Length).ThenBy(x => x.Name, StringComparer.Ordinal)
            .Select(x => x.Name).Distinct(StringComparer.Ordinal).Take(max).ToList();
    }

    /// <summary>Calculates a stored recipe and persists an immutable snapshot (idempotent on the input hash); re-pins linked images and queues re-indexing.</summary>
    public async Task<RecipeCalculationResponse> CalculateRecipeAsync(Recipe recipe, Guid actorId, bool actorIsAdmin, CancellationToken cancellationToken)
    {
        if (!actorIsAdmin && recipe.OwnerUserId != actorId)
            throw new ApiProblemException(404, "recipe_not_found", "Recipe not found", "The recipe does not exist or is not yours to calculate.");
        var data = RequireData();
        var rows = WithAliases(RowsFromRecipe(recipe), data, provider.Curation);
        var resolutions = Resolve(data, rows, provider.Curation);
        if (resolutions.Any(r => r.Status != "resolved"))
            throw new ApiProblemException(422, "materials_unresolved", "Materials could not be resolved",
                "Every row must name a material in the materials list and carry an amount before the recipe's chemistry is stored.",
                new Dictionary<string, object?> { ["resolutions"] = resolutions });

        var result = GlazeChemistry.CalculateRecipe(rows, data)
                     ?? throw new ApiProblemException(422, "recipe_chemistry_unavailable", "No chemistry", "The recipe's amounts add up to zero or less.");
        if (result.Chem.FluxSum <= GlazeChemistry.Zero)
            throw new ApiProblemException(422, "zero_flux", "UMF undefined", GlazeChemistry.NoFluxWarning, new Dictionary<string, object?> { ["warnings"] = result.Chem.Warnings });

        var versions = Versions(data);
        var inputHash = InputHash(versions, result.Lines);
        var umfJson = JsonSerializer.Serialize(result.Chem.Umf);
        using var umfDoc = JsonDocument.Parse(umfJson);
        var schema = ChemistrySchema.ValidateUmf(umfDoc.RootElement);

        var existing = await db.RecipeCalculationResults.SingleOrDefaultAsync(x => x.RecipeId == recipe.Id && x.CalculationProfile == versions.CalculationProfile && x.InputHash == inputHash, cancellationToken);
        var created = existing is null;
        var snapshot = existing ?? new RecipeCalculationResult
        {
            Id = Guid.CreateVersion7(),
            RecipeId = recipe.Id,
            CalculationProfile = versions.CalculationProfile,
            CalculationVersion = versions.CalculationVersion,
            MaterialAnalysisVersion = versions.MaterialsVersion,
            ChemistrySchemaVersion = ChemistrySchema.Version,
            InputHash = inputHash,
            OxideWeightPercentJson = JsonSerializer.Serialize(result.Chem.AnalysisPct),
            RawMolesJson = JsonSerializer.Serialize(result.Chem.Moles),
            FluxTotal = (decimal)result.Chem.FluxSum,
            UmfJson = umfJson,
            OxideGroupsJson = JsonSerializer.Serialize(new { displayGroups = GlazeChemistry.DisplayGroups(result.Chem).Select(g => new DisplayGroupResponse(g.Key, g.Label, g.Oxides.Select(o => new OxideValue(o.Key, o.Value)).ToList())), molePct = result.Chem.MolePct }, Json),
            RatiosJson = JsonSerializer.Serialize(new { ratios = result.Chem.Ratios, cte = result.Chem.Cte, cteBand = GlazeChemistry.CteBand(result.Chem.Cte) }, Json),
            WarningsJson = JsonSerializer.Serialize(result.Chem.Warnings.Concat(schema.Warnings)),
            InputJson = JsonSerializer.Serialize(new { rows, lines = result.Lines, resolutions, versions }, Json)
        };
        if (created) db.RecipeCalculationResults.Add(snapshot);

        var repinned = await RepinImagesAsync(recipe.Id, snapshot, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Recipe {RecipeId} calculated ({Created}); snapshot {SnapshotId}; {Repinned} images re-pinned", recipe.Id, created ? "new snapshot" : "existing snapshot", snapshot.Id, repinned);
        return new RecipeCalculationResponse(snapshot.Id, recipe.Id, recipe.Code, created, versions, inputHash, result, schema.Warnings, repinned, snapshot.CreatedAt);
    }

    /// <summary>Images linked to the recipe that wait for chemistry, or are pinned to an older snapshot of it, move to the new snapshot and are re-indexed.</summary>
    private async Task<int> RepinImagesAsync(Guid recipeId, RecipeCalculationResult snapshot, CancellationToken cancellationToken)
    {
        var assets = await db.ImageAssets.Include(x => x.CalculationResult)
            .Where(x => x.Recipes.Any(r => r.RecipeId == recipeId)).ToListAsync(cancellationToken);
        var count = 0;
        foreach (var asset in assets)
        {
            var waiting = asset.ChemistryStatus is ChemistryStatus.Pending or ChemistryStatus.Missing && asset.CalculationResultId is null;
            var older = asset.CalculationResult is { } current && current.RecipeId == recipeId && current.Id != snapshot.Id;
            if (!waiting && !older) continue;
            asset.CalculationResultId = snapshot.Id;
            asset.ChemistryStatus = ChemistryStatus.Linked;
            asset.SourceVersion++;
            asset.UpdatedAt = DateTimeOffset.UtcNow;
            asset.ConcurrencyStamp = Guid.NewGuid();
            if (asset.ApprovalStatus == ApprovalStatus.Approved) await IndexJobs.EnqueueUpsertAsync(db, asset.Id, cancellationToken);
            count++;
        }
        return count;
    }

    /// <summary>Material rows stored on a recipe: lab-coded variations keep them in "full"; other recipes in "materials" or "rows".</summary>
    public static IReadOnlyList<RecipeRowInput> RowsFromRecipe(Recipe recipe)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(recipe.RecipeJson) ? "{}" : recipe.RecipeJson);
        var root = doc.RootElement;
        JsonElement array = default;
        var found = root.ValueKind == JsonValueKind.Object && (root.TryGetProperty("full", out array) || root.TryGetProperty("materials", out array) || root.TryGetProperty("rows", out array)) && array.ValueKind == JsonValueKind.Array;
        if (!found || array.GetArrayLength() == 0)
            throw new ApiProblemException(422, "recipe_has_no_materials", "Recipe has no material rows", "This recipe stores no material rows to calculate from.");
        var rows = new List<RecipeRowInput>();
        var units = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in array.EnumerateArray())
        {
            string? name = null;
            foreach (var key in new[] { "rawName", "material", "name" })
                if (e.TryGetProperty(key, out var n) && n.ValueKind == JsonValueKind.String) { name = n.GetString(); break; }
            JsonElement? amount = e.TryGetProperty("amount", out var a) ? a.Clone() : null;
            if (e.TryGetProperty("unit", out var u) && u.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(u.GetString())) units.Add(u.GetString()!.Trim());
            rows.Add(new RecipeRowInput(name, amount));
        }
        // The calculator reads amounts as plain numbers, so rows in different units cannot be combined safely.
        if (units.Count > 1) throw new ApiProblemException(422, "mixed_units", "Mixed units", $"Rows use different units ({string.Join(", ", units)}). Store every row in the same unit.");
        return rows;
    }

    public static string InputHash(UmfVersions v, IReadOnlyList<RecipeLine> lines)
    {
        var canonical = $"{v.CalculationProfile}|{v.CalculationVersion}|{v.OxidesVersion}|{v.MaterialsVersion}|" + string.Join(";", lines.Select(l => $"{l.MaterialId}={l.PartsPct.ToString("R", CultureInfo.InvariantCulture)}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static ApiProblemException Validation(string field, string message) =>
        new(400, "validation_error", "Validation failed", message, new Dictionary<string, object?> { ["errors"] = new Dictionary<string, string[]> { [field] = [message] } });
}
