using System.Globalization;
using System.Text.Json;
using Glazion.Api.Chemistry;
using Glazion.Api.Contracts;
using Glazion.Api.Data;
using Glazion.Api.Domain;
using Glazion.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Glazion.Api.Retrieval;

/// <summary>A material row as typed into a search, with what it resolved to. Kept on the run for Match check.</summary>
public sealed record SearchedMaterial(string? RawName, string? MaterialId, string? Name, decimal? Amount, string? Unit);

// Match check (admin only): each search beside what it matched, and where each matched tile came from, so an admin can
// confirm that what a person typed and what the system suggested agree chemically before results go public.

public sealed record MatchCheckPerson(Guid Id, string Username, string? DisplayName, string? ParticipantCode);

/// <summary>A material row on a search or a recipe. Kind is the material's category (colorant, stain, clay…); null when the name is not in the list.</summary>
public sealed record MatchCheckMaterial(string Name, decimal? Amount, string? Unit, string? Kind);

public sealed record MatchCheckTile(Guid ImageId, Uri? ThumbnailUrl, string? RecipeCode, string? RecipeName);

/// <summary>NotRelevant: how many results the person marked as not relevant.</summary>
public sealed record SearchCheckSummary(Guid Id, DateTimeOffset CreatedAt, MatchCheckPerson? Requester, string QueryType, string? Name, string? RecipeCode,
    IReadOnlyList<string> Materials, IReadOnlyList<Colourant>? Colourants, int ResultCount, double? TopScore, string? TopBand, MatchCheckTile? Top, int NotRelevant);

public sealed record SearchCheckDetail(Guid Id, DateTimeOffset CreatedAt, MatchCheckPerson? Requester, SearchCheckQuery Query, IReadOnlyList<SearchCheckResult> Results,
    IReadOnlyList<string> Warnings, string RankerVersion, int CandidateCount, int? LatencyMs);

/// <summary>
/// What was searched. MaterialsAsTyped: the rows the person typed (recipe searches from 2026-10-27 on); otherwise the
/// searched recipe's rows as they are now, or none (a UMF search, or an older recipe search).
/// </summary>
public sealed record SearchCheckQuery(string Type, string? Name, Guid? RecipeId, string? RecipeCode, string? RecipeName, IReadOnlyList<MatchCheckMaterial>? Materials,
    bool MaterialsAsTyped, IReadOnlyDictionary<string, decimal>? Umf, IReadOnlyList<Colourant>? Colourants, decimal? ConeMin, decimal? ConeMax, string? Atmosphere,
    bool Strict, IReadOnlyList<string> ClayBodies);

/// <summary>Shown: "closest" (the big card), "similar" (the row of other glazes) or "ranked" (further down the full list).</summary>
public sealed record SearchCheckResult(int? Rank, string Shown, double Score, string ConfidenceBand, SearchScoreBreakdown ScoreBreakdown, IReadOnlyList<string> MatchedFields,
    IReadOnlyList<string> Limitations, string? Feedback, string? FeedbackNote, ImageTrace? Image, IReadOnlyList<OxideComparison> Comparison);

/// <summary>Where a tile came from (file, import batch or participant) and the recipe and chemistry it carries.</summary>
public sealed record ImageTrace(Guid ImageId, Uri? DisplayUrl, Uri? ThumbnailUrl, ImageSourceType Source, string? FileName, string? RecordId, string? BatchName,
    MatchCheckPerson? Owner, string? ClayBody, decimal? ConeMin, decimal? ConeMax, string? Atmosphere, ApprovalStatus ApprovalStatus,
    IReadOnlyList<TracedRecipe> Recipes, TracedChemistry? Chemistry, IReadOnlyList<Colourant>? Colourants, Uri? BackUrl = null, string? MadeByUsername = null);

/// <summary>Materials: the whole recipe as mixed. Additions: what a variation adds to its base (empty for other recipes).</summary>
public sealed record TracedRecipe(Guid Id, string? Code, string Name, bool Verified, string Relationship, string? BaseCode, string? BaseName,
    IReadOnlyList<MatchCheckMaterial> Materials, IReadOnlyList<MatchCheckMaterial> Additions);

/// <summary>
/// Supplied: a UMF imported as numbers, not calculated from materials. ComparedAtSearch: true when this is the snapshot
/// the search compared; false when the tile has been re-calculated since; null when the search did not record it.
/// </summary>
public sealed record TracedChemistry(Guid CalculationResultId, bool Supplied, IReadOnlyDictionary<string, decimal> Umf, bool? ComparedAtSearch);

/// <summary>One oxide, searched against tile (UMF). Group: flux, stabiliser, glass, colourant, opacifier or other. Difference is tile minus searched.</summary>
public sealed record OxideComparison(string Oxide, string Group, decimal? Searched, decimal? Tile, decimal? Difference);

public sealed class MatchCheckService(GlazionDbContext db, IR2ObjectStorage storage, ChemistryDataProvider chemistry)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Metallic oxides that colour a glaze: what Toluwalope calls colorants, alongside stains.</summary>
    private static readonly HashSet<string> ColourantOxides = new(StringComparer.Ordinal) { "Fe2O3", "FeO", "CoO", "CuO", "Cr2O3", "MnO", "NiO", "V2O5", "TiO2" };
    private static readonly HashSet<string> OpacifierOxides = new(StringComparer.Ordinal) { "ZrO2", "SnO2" };
    private static readonly Dictionary<string, int> OxideRank = ChemistrySchema.OxideOrder.Select((x, i) => (x, i)).ToDictionary(x => x.x, x => x.i, StringComparer.Ordinal);

    /// <summary>Newest first. notRelevant=true: only searches where the person marked a result as not relevant. q matches the person's username or name.</summary>
    public async Task<PagedResult<SearchCheckSummary>> ListAsync(Guid? userId, string? type, bool notRelevant, string? q, string? cursor, int? pageSize, CancellationToken cancellationToken)
    {
        var runs = db.SearchRuns.AsNoTracking();
        if (userId is { } user) runs = runs.Where(x => x.RequesterUserId == user);
        if (!string.IsNullOrWhiteSpace(type))
        {
            var t = type.Trim().ToLowerInvariant();
            runs = runs.Where(x => x.QueryType == t);
        }
        if (notRelevant) runs = runs.Where(x => db.SearchFeedback.Any(f => f.SearchRunId == x.Id && f.Feedback == "not_relevant"));
        if (!string.IsNullOrWhiteSpace(q))
        {
            var text = q.Trim().ToLower();
            runs = runs.Where(x => db.Users.Any(u => u.Id == x.RequesterUserId && (u.Username.ToLower().Contains(text) || u.DisplayName.ToLower().Contains(text))));
        }
        return await Paging.PageAsync(runs.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id), cursor, pageSize,
            async page => (IReadOnlyList<SearchCheckSummary>)await SummariseAsync(page, cancellationToken), cancellationToken);
    }

    public async Task<SearchCheckDetail?> GetAsync(Guid searchId, CancellationToken cancellationToken)
    {
        var run = await db.SearchRuns.AsNoTracking().SingleOrDefaultAsync(x => x.Id == searchId, cancellationToken);
        if (run is null) return null;
        var people = await PeopleAsync([run.RequesterUserId], cancellationToken);
        var snapshot = Snapshot.Parse(run.QuerySnapshotJson);
        var data = chemistry.TryGet(out var loaded, out _) ? loaded : null;

        // The closest match is items[0]; the row of other glazes is "similar"; the rest are further down the full list.
        var ranked = Deserialize<List<SearchResultItem>>(run.ResultsJson) ?? [];
        var similarIds = snapshot.Similar.Select(x => x.ImageId).ToHashSet();
        var shown = ranked.Select((x, i) => (Item: x, Rank: (int?)x.Rank, Shown: i == 0 ? "closest" : similarIds.Contains(x.ImageId) ? "similar" : "ranked")).ToList();
        var rankedIds = ranked.Select(x => x.ImageId).ToHashSet();
        shown.AddRange(snapshot.Similar.Where(x => !rankedIds.Contains(x.ImageId)).Select(x => (Item: x, Rank: (int?)null, Shown: "similar")));

        var imageIds = shown.Select(x => x.Item.ImageId).Distinct().ToList();
        var traces = await TraceAsync(imageIds, snapshot.Pinned, cancellationToken);
        var feedback = (await db.SearchFeedback.AsNoTracking().Where(x => x.SearchRunId == run.Id).ToListAsync(cancellationToken))
            .GroupBy(x => x.ImageId).ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.UpdatedAt).First());

        // What was searched: the typed rows when the run kept them, else the searched recipe as it is now.
        IReadOnlyList<MatchCheckMaterial>? materials = snapshot.Typed?.Select(m => new MatchCheckMaterial(m.Name ?? m.RawName ?? "?", m.Amount, m.Unit, KindOf(data, m.MaterialId, m.Name ?? m.RawName))).ToList();
        var clayBodies = Deserialize<FiltersShape>(run.FiltersJson)?.ClayBodies ?? [];
        string? recipeName = null;
        if (snapshot.RecipeId is { } recipeId)
        {
            var recipe = await db.Recipes.AsNoTracking().SingleOrDefaultAsync(x => x.Id == recipeId, cancellationToken);
            recipeName = recipe?.Name;
            if (materials is null && recipe is not null) materials = RecipeMaterials(recipe, data);
        }

        var query = new SearchCheckQuery(run.QueryType, snapshot.Name, snapshot.RecipeId, snapshot.RecipeCode, recipeName, materials, snapshot.Typed is not null,
            snapshot.Umf, snapshot.Colourants, snapshot.ConeMin, snapshot.ConeMax, snapshot.Atmosphere, snapshot.Strict, clayBodies);
        var results = shown.Select(x =>
        {
            var trace = traces.GetValueOrDefault(x.Item.ImageId);
            feedback.TryGetValue(x.Item.ImageId, out var f);
            return new SearchCheckResult(x.Rank, x.Shown, x.Item.Score, x.Item.ConfidenceBand, x.Item.ScoreBreakdown, x.Item.MatchedFields, x.Item.Limitations,
                f?.Feedback, f?.Note, trace, Compare(snapshot.Umf, trace?.Chemistry?.Umf));
        }).ToList();
        return new SearchCheckDetail(run.Id, run.CreatedAt, people.GetValueOrDefault(run.RequesterUserId), query, results,
            Deserialize<List<string>>(run.WarningsJson) ?? [], run.RankerVersion, run.CandidateCount, run.LatencyMs);
    }

    /// <summary>
    /// Where each image came from and the recipe and chemistry it carries. With <paramref name="pinned"/> (image →
    /// snapshot a search compared), the chemistry is that snapshot; otherwise the image's current one.
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, ImageTrace>> TraceAsync(IReadOnlyCollection<Guid> imageIds, IReadOnlyDictionary<Guid, Guid?>? pinned, CancellationToken cancellationToken)
    {
        if (imageIds.Count == 0) return new Dictionary<Guid, ImageTrace>();
        var assets = await db.ImageAssets.AsNoTracking()
            .Include(x => x.ImportRecord).ThenInclude(x => x!.Batch)
            .Include(x => x.Upload).ThenInclude(x => x!.TileTask).ThenInclude(x => x!.AssignedUser)
            .Include(x => x.CalculationResult)
            .Include(x => x.Recipes).ThenInclude(x => x.Recipe).ThenInclude(x => x!.BaseGlaze)
            .AsSplitQuery()
            .Where(x => imageIds.Contains(x.Id)).ToListAsync(cancellationToken);
        var earlier = pinned is null ? [] : pinned
            .Where(kv => kv.Value is { } id && assets.Any(a => a.Id == kv.Key && a.CalculationResultId != id))
            .Select(kv => kv.Value!.Value).Distinct().ToList();
        var snapshots = earlier.Count == 0 ? new Dictionary<Guid, RecipeCalculationResult>()
            : await db.RecipeCalculationResults.AsNoTracking().Where(x => earlier.Contains(x.Id)).ToDictionaryAsync(x => x.Id, cancellationToken);
        var people = await PeopleAsync(assets.Where(x => x.OwnerUserId is not null).Select(x => x.OwnerUserId!.Value).Distinct().ToList(), cancellationToken);
        var data = chemistry.TryGet(out var loaded, out _) ? loaded : null;

        var result = new Dictionary<Guid, ImageTrace>(assets.Count);
        foreach (var asset in assets)
        {
            RecipeCalculationResult? snapshot;
            bool? comparedAtSearch = null;
            if (pinned is not null && pinned.TryGetValue(asset.Id, out var pinnedId))
            {
                snapshot = pinnedId is not { } id ? null : asset.CalculationResultId == id ? asset.CalculationResult : snapshots.GetValueOrDefault(id);
                comparedAtSearch = true;
            }
            else snapshot = asset.CalculationResult;
            TracedChemistry? traced = null;
            if (snapshot is not null && ParseUmf(snapshot.UmfJson) is { } umf)
                traced = new TracedChemistry(snapshot.Id, snapshot.CalculationProfile == ChemistrySchema.ProvidedUmfProfile, umf, comparedAtSearch);

            var recipes = asset.Recipes.Where(x => x.Recipe is not null).OrderByDescending(x => x.Verified).ThenBy(x => x.CreatedAt)
                .Select(x => new TracedRecipe(x.RecipeId, x.Recipe!.Code, x.Recipe.Name, x.Verified, x.RelationshipType.ToString(), x.Recipe.BaseGlaze?.Code, x.Recipe.BaseGlaze?.Name,
                    RecipeMaterials(x.Recipe, data), Rows(x.Recipe.AdditionsJson, null, data)))
                .ToList();
            var code = recipes.Select(x => x.Code).FirstOrDefault(x => x is not null) ?? asset.Id.ToString("D");
            Uri? display = null, thumbnail = null, back = null;
            if (asset.QualityStatus != QualityStatus.Failed)
            {
                var ext = ObjectKeyPolicy.ExtensionFor(asset.DeclaredContentType);
                display = await storage.CreateGetUrlAsync(asset.DisplayKey ?? asset.OriginalKey, $"{code}.{(asset.DisplayKey is null ? ext : "webp")}", cancellationToken);
                thumbnail = await storage.CreateGetUrlAsync(asset.ThumbnailKey ?? asset.OriginalKey, $"{code}-thumb.{(asset.ThumbnailKey is null ? ext : "webp")}", cancellationToken);
                back = await TilePhotoUrls.BackAsync(storage, asset, code, cancellationToken);
            }
            result[asset.Id] = new ImageTrace(asset.Id, display, thumbnail, asset.SourceType,
                asset.ImportRecord?.FileName ?? asset.Upload?.OriginalFileName, asset.ImportRecord?.ExternalRecordId, asset.ImportRecord?.Batch?.Name,
                asset.OwnerUserId is { } owner ? people.GetValueOrDefault(owner) : null,
                asset.ClayBody, asset.ConeMin, asset.ConeMax, asset.Atmosphere, asset.ApprovalStatus, recipes, traced,
                data is null ? null : Colourants.ForAsset(asset, data, chemistry.Curation), back, asset.Upload?.TileTask?.AssignedUser?.Username);
        }
        return result;
    }

    /// <summary>Every oxide either side reports, in schema order, with the tile's value minus the searched one.</summary>
    public static IReadOnlyList<OxideComparison> Compare(IReadOnlyDictionary<string, decimal>? searched, IReadOnlyDictionary<string, decimal>? tile)
    {
        if (searched is null && tile is null) return [];
        var oxides = (searched?.Where(x => x.Value != 0).Select(x => x.Key) ?? []).Union(tile?.Where(x => x.Value != 0).Select(x => x.Key) ?? [], StringComparer.Ordinal)
            .OrderBy(x => OxideRank.GetValueOrDefault(x, int.MaxValue)).ThenBy(x => x, StringComparer.Ordinal);
        return oxides.Select(oxide =>
        {
            decimal? s = searched is null ? null : searched.GetValueOrDefault(oxide);
            decimal? t = tile is null ? null : tile.GetValueOrDefault(oxide);
            return new OxideComparison(oxide, GroupOf(oxide), s, t, s is { } a && t is { } b ? Math.Round(b - a, 4) : null);
        }).ToList();
    }

    public static string GroupOf(string oxide) =>
        ChemistrySchema.FluxOxides.Contains(oxide) ? "flux"
        : oxide is "Al2O3" or "B2O3" ? "stabiliser"
        : oxide == "SiO2" ? "glass"
        : ColourantOxides.Contains(oxide) ? "colourant"
        : OpacifierOxides.Contains(oxide) ? "opacifier"
        : "other";

    // ---- Helpers ----

    private async Task<List<SearchCheckSummary>> SummariseAsync(List<SearchRun> page, CancellationToken cancellationToken)
    {
        var people = await PeopleAsync(page.Select(x => x.RequesterUserId).Distinct().ToList(), cancellationToken);
        var runIds = page.Select(x => x.Id).ToList();
        var notRelevant = await db.SearchFeedback.AsNoTracking().Where(x => runIds.Contains(x.SearchRunId) && x.Feedback == "not_relevant")
            .GroupBy(x => x.SearchRunId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, cancellationToken);
        var tops = page.Select(run => (run, top: (Deserialize<List<SearchResultItem>>(run.ResultsJson) ?? []).FirstOrDefault())).ToList();
        var topIds = tops.Where(x => x.top is not null).Select(x => x.top!.ImageId).Distinct().ToList();
        var assets = topIds.Count == 0 ? [] : await db.ImageAssets.AsNoTracking().Include(x => x.Recipes).ThenInclude(x => x.Recipe)
            .Where(x => topIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, cancellationToken);

        var result = new List<SearchCheckSummary>(page.Count);
        foreach (var (run, top) in tops)
        {
            var snapshot = Snapshot.Parse(run.QuerySnapshotJson);
            MatchCheckTile? tile = null;
            if (top is not null && assets.TryGetValue(top.ImageId, out var asset))
            {
                var recipe = asset.Recipes.OrderByDescending(x => x.Verified).ThenBy(x => x.CreatedAt).Select(x => x.Recipe).FirstOrDefault(x => x is not null);
                var thumbnail = asset.QualityStatus == QualityStatus.Failed ? null : await storage.CreateGetUrlAsync(asset.ThumbnailKey ?? asset.OriginalKey, cancellationToken);
                tile = new MatchCheckTile(asset.Id, thumbnail, recipe?.Code, recipe?.Name);
            }
            result.Add(new SearchCheckSummary(run.Id, run.CreatedAt, people.GetValueOrDefault(run.RequesterUserId), run.QueryType, snapshot.Name, snapshot.RecipeCode,
                snapshot.Typed?.Select(m => m.Name ?? m.RawName ?? "?").ToList() ?? [], snapshot.Colourants, run.ResultCount, top?.Score, top?.ConfidenceBand, tile,
                notRelevant.GetValueOrDefault(run.Id)));
        }
        return result;
    }

    private async Task<Dictionary<Guid, MatchCheckPerson>> PeopleAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken) =>
        ids.Count == 0 ? [] : await db.Users.AsNoTracking().Where(x => ids.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, x => new MatchCheckPerson(x.Id, x.Username, string.IsNullOrWhiteSpace(x.DisplayName) ? null : x.DisplayName, x.ParticipantCode), cancellationToken);

    /// <summary>A recipe's rows as mixed: a lab-coded variation's "full" (base + additions), else "materials" or "rows".</summary>
    /// <summary>A task's recipe as the participant mixes it: base rows, additions and the whole, each row with its kind. The recipe's BaseGlaze must be loaded.</summary>
    public Glazion.Api.Tasks.TaskRecipeDto RecipeFor(Recipe recipe)
    {
        var data = chemistry.TryGet(out var loaded, out _) ? loaded : null;
        return new Glazion.Api.Tasks.TaskRecipeDto(string.IsNullOrWhiteSpace(recipe.Description) ? null : recipe.Description, recipe.BaseGlaze?.Code, recipe.BaseGlaze?.Name,
            recipe.BaseGlaze is null ? [] : Rows(recipe.BaseGlaze.FormulationJson, null, data), Rows(recipe.AdditionsJson, null, data), RecipeMaterials(recipe, data),
            recipe.FiringCone, recipe.FiringAtmosphere);
    }

    private IReadOnlyList<MatchCheckMaterial> RecipeMaterials(Recipe recipe, ChemistryData? data)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(recipe.RecipeJson) ? "{}" : recipe.RecipeJson);
            var root = doc.RootElement;
            foreach (var key in new[] { "full", "materials", "rows" })
                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty(key, out var array) && array.ValueKind == JsonValueKind.Array)
                    return Rows(null, array, data);
        }
        catch (JsonException) { }
        return [];
    }

    private IReadOnlyList<MatchCheckMaterial> Rows(string? json, JsonElement? element, ChemistryData? data)
    {
        try
        {
            using var doc = json is null ? null : JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "[]" : json);
            var array = doc?.RootElement ?? element!.Value;
            if (array.ValueKind != JsonValueKind.Array) return [];
            var rows = new List<MatchCheckMaterial>();
            foreach (var e in array.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object) continue;
                string? name = null;
                foreach (var key in new[] { "rawName", "material", "name" })
                    if (e.TryGetProperty(key, out var n) && n.ValueKind == JsonValueKind.String) { name = n.GetString(); break; }
                if (string.IsNullOrWhiteSpace(name)) continue;
                decimal? amount = e.TryGetProperty("amount", out var a) ? a.ValueKind switch
                {
                    JsonValueKind.Number => a.GetDecimal(),
                    JsonValueKind.String when decimal.TryParse(a.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
                    _ => null
                } : null;
                var unit = e.TryGetProperty("unit", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null;
                rows.Add(new MatchCheckMaterial(name.Trim(), amount, unit, KindOf(data, null, name)));
            }
            return rows;
        }
        catch (JsonException) { return []; }
    }

    /// <summary>The material's category (colorant, stain, clay…), or null when it is not in the list.</summary>
    private string? KindOf(ChemistryData? data, string? materialId, string? name)
    {
        if (data is null) return null;
        var material = materialId is not null && data.MaterialsById.TryGetValue(materialId, out var byId) ? byId
            : string.IsNullOrWhiteSpace(name) ? null : GlazeChemistry.FindByName(data, name);
        return material is null ? null : MaterialSearch.CategoryOf(material, chemistry.Curation);
    }

    private sealed record FiltersShape(List<string>? ClayBodies);

    private static IReadOnlyDictionary<string, decimal>? ParseUmf(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            var validation = ChemistrySchema.ValidateUmf(doc.RootElement);
            return validation.IsValid ? validation.Umf : null;
        }
        catch (JsonException) { return null; }
    }

    private static T? Deserialize<T>(string json) where T : class
    {
        try { return JsonSerializer.Deserialize<T>(string.IsNullOrWhiteSpace(json) ? "null" : json, Json); }
        catch (JsonException) { return null; }
    }

    /// <summary>The parts of a run's QuerySnapshotJson that Match check reads. Fields a run predates are null or empty.</summary>
    private sealed record Snapshot(string? Name, IReadOnlyList<SearchedMaterial>? Typed, Guid? RecipeId, string? RecipeCode, IReadOnlyDictionary<string, decimal>? Umf,
        IReadOnlyList<Colourant>? Colourants, decimal? ConeMin, decimal? ConeMax, string? Atmosphere, bool Strict,
        IReadOnlyList<SearchResultItem> Similar, IReadOnlyDictionary<Guid, Guid?>? Pinned)
    {
        public static Snapshot Parse(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
                var root = doc.RootElement;
                JsonElement Get(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v : default;
                string? Str(JsonElement e) => e.ValueKind == JsonValueKind.String ? e.GetString() : null;
                decimal? Num(JsonElement e) => e.ValueKind == JsonValueKind.Number ? e.GetDecimal() : null;
                T? As<T>(JsonElement e) where T : class => e.ValueKind is JsonValueKind.Object or JsonValueKind.Array ? e.Deserialize<T>(Json) : null;

                var query = Get(root, "query");
                var input = Get(root, "input");
                var firing = Get(query, "firing");
                var pinned = Get(root, "pinned");
                IReadOnlyDictionary<Guid, Guid?>? pins = pinned.ValueKind != JsonValueKind.Array ? null : pinned.EnumerateArray()
                    .Where(p => Get(p, "imageId").ValueKind == JsonValueKind.String)
                    .ToDictionary(p => Get(p, "imageId").GetGuid(), p => Get(p, "calculationResultId") is { ValueKind: JsonValueKind.String } c ? c.GetGuid() : (Guid?)null);
                return new Snapshot(Str(Get(input, "name")), As<List<SearchedMaterial>>(Get(input, "materials")),
                    Get(query, "recipeId") is { ValueKind: JsonValueKind.String } r ? r.GetGuid() : (Guid?)null, Str(Get(query, "recipeCode")),
                    As<Dictionary<string, decimal>>(Get(query, "umf")), As<List<Colourant>>(Get(query, "colourants")),
                    Num(Get(firing, "coneMin")), Num(Get(firing, "coneMax")), Str(Get(firing, "atmosphere")), Get(query, "strict").ValueKind == JsonValueKind.True,
                    As<List<SearchResultItem>>(Get(root, "similar")) ?? [], pins);
            }
            catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException)
            {
                return new Snapshot(null, null, null, null, null, null, null, null, null, false, [], null);
            }
        }
    }
}
