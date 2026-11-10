using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Glazion.Api.Chemistry;
using Glazion.Api.Contracts;
using Glazion.Api.Data;
using Glazion.Api.Domain;
using Glazion.Api.Infrastructure;
using Glazion.Api.Retrieval;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Glazion.Api.Chat;

/// <summary>What a tool gave back: JSON for the model, one line for the user, and sources to cite.</summary>
public sealed record ToolOutcome(string ResultJson, ChatToolCallDto Call, IReadOnlyList<ChatSourceDto> Sources);

/// <summary>
/// The chat's tools (@glazion/types ChatToolName). Each runs one of the API's own services as the signed-in user, with
/// the same visibility rules as the matching endpoint: a participant only sees their own uploads and recipes they may use.
/// </summary>
public sealed class ChatTools(GlazionDbContext db, UmfCalculationService umf, RecipeImageSearchService search, IOptions<FeatureOptions> features)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const int MaxResultChars = 6000;

    public static IReadOnlyList<ToolSpec> Specs { get; } =
    [
        new("calculate_umf", "Calculate the unity molecular formula (UMF), ratios, expansion and warnings for a glaze recipe given as materials and amounts.",
            Schema(new JsonObject
            {
                ["rows"] = ArrayOf(Object(new JsonObject { ["material"] = Str("Material name, e.g. Custer Feldspar"), ["amount"] = Num("Amount, usually percent or grams") }, "material", "amount"), "Recipe rows"),
                ["cone"] = Str("Firing cone, e.g. 6 or 10 or 06, to check limits")
            }, "rows")),
        new("search_recipe_images", "Find photos of fired test tiles whose chemistry and firing match a recipe. Give a stored recipe (recipeId or lab recipeCode such as A03-B2-009), or raw materials, or a UMF.",
            Schema(new JsonObject
            {
                ["recipeId"] = Str("Stored recipe id"),
                ["recipeCode"] = Str("Lab recipe code, e.g. P01-B1-001"),
                ["materials"] = ArrayOf(Object(new JsonObject { ["rawName"] = Str("Material name"), ["amount"] = Num("Amount in percent") }, "rawName", "amount"), "Raw recipe rows"),
                ["umf"] = new JsonObject { ["type"] = "object", ["description"] = "UMF as oxide -> value, e.g. {\"SiO2\": 3.2, \"Al2O3\": 0.35}" },
                ["coneMin"] = Num("Lowest cone"), ["coneMax"] = Num("Highest cone"),
                ["atmosphere"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("oxidation", "reduction", "neutral") },
                ["topK"] = new JsonObject { ["type"] = "integer", ["description"] = "Results to return (1-12)" }
            })),
        new("get_recipe", "Look up a stored glaze recipe with its materials, additions, outcome notes and calculated chemistry.",
            Schema(new JsonObject { ["recipeId"] = Str("Recipe id"), ["code"] = Str("Lab recipe code"), ["name"] = Str("Part of the recipe name") })),
        new("get_upload_status", "Status of the user's own photo uploads: one by id, or the most recent ones.",
            Schema(new JsonObject { ["uploadId"] = Str("Upload id; leave empty for the latest uploads") })),
        new("get_material", "Look up a ceramic material's oxide analysis and loss on ignition.",
            Schema(new JsonObject { ["name"] = Str("Material name, e.g. Nepheline Syenite") }, "name"))
    ];

    /// <summary>
    /// The tools a requester may use: a regular user gets no search by UMF and no UMF of a stored recipe (SearchVisibility).
    /// The UMF calculator stays, as it only works on the recipe they give.
    /// </summary>
    public static IReadOnlyList<ToolSpec> SpecsFor(SearchRequester requester) => requester.IsAdmin ? Specs : UserSpecs;

    private static readonly IReadOnlyList<ToolSpec> UserSpecs = Specs.Select(s => s.Name == "search_recipe_images"
        ? s with
        {
            Description = "Find photos of fired test tiles that match a recipe. Give a stored recipe (recipeId or lab recipeCode such as A03-B2-009), or raw materials.",
            Parameters = Without(s.Parameters, "umf")
        }
        : s.Name == "get_recipe" ? s with { Description = "Look up a stored glaze recipe with its materials, additions and outcome notes." } : s).ToList();

    private static JsonObject Without(JsonObject schema, string property)
    {
        var copy = (JsonObject)schema.DeepClone();
        (copy["properties"] as JsonObject)?.Remove(property);
        return copy;
    }

    public async Task<ToolOutcome> RunAsync(string name, string argumentsJson, SearchRequester requester, CancellationToken cancellationToken)
    {
        JsonObject args;
        try { args = JsonNode.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson) as JsonObject ?? []; }
        catch (JsonException) { return Failed(name, "The tool arguments were not valid JSON."); }
        try
        {
            return name switch
            {
                "calculate_umf" => CalculateUmf(args),
                "search_recipe_images" => await SearchImagesAsync(args, requester, cancellationToken),
                "get_recipe" => await GetRecipeAsync(args, requester, cancellationToken),
                "get_upload_status" => await GetUploadsAsync(args, requester, cancellationToken),
                "get_material" => GetMaterial(args),
                _ => Failed(name, $"Unknown tool {name}.")
            };
        }
        catch (ApiProblemException ex) { return Failed(name, string.IsNullOrWhiteSpace(ex.Message) ? ex.Title : ex.Message); }
    }

    private ToolOutcome CalculateUmf(JsonObject args)
    {
        var rows = (args["rows"] as JsonArray ?? [])
            .Select(r => new RecipeRowInput(r?["material"]?.GetValue<string>(), r?["amount"] is JsonNode a ? JsonSerializer.SerializeToElement(a, Json) : null))
            .ToList();
        if (rows.Count == 0) return Failed("calculate_umf", "Give at least one material with an amount.");
        var cone = Text(args, "cone");
        var result = umf.Calculate(new UmfCalculateRequest(rows, cone));
        if (result.Result is null)
            return Done("calculate_umf", new { error = "No row could be calculated.", skipped = result.SkippedRows }, $"Tried to calculate a {rows.Count}-material recipe, but no material was recognised", []);
        var chem = result.Result;
        var outOfRange = result.Limits
            .Where(l => chem.Umf.TryGetValue(l.Oxide, out var v) ? v < l.MinUmf || v > l.MaxUmf : l.MinUmf > 0)
            .Select(l => new { l.Oxide, value = Round(chem.Umf.GetValueOrDefault(l.Oxide)), l.MinUmf, l.MaxUmf, l.Notes });
        return Done("calculate_umf", new
        {
            cone = result.Cone,
            umf = chem.Umf.ToDictionary(x => x.Key, x => Round(x.Value)),
            ratios = chem.Ratios,
            expansion = Round(chem.Cte),
            warnings = chem.Warnings,
            outsideConeLimits = outOfRange,
            skippedRows = result.SkippedRows,
            suggestions = result.Result.Suggestions.Take(4).Select(s => new { s.Oxide, s.Direction, s.Target, materials = s.Materials.Select(m => m.Name) })
        }, $"Calculated the UMF of a {rows.Count}-material recipe{(cone is null ? "" : $" at cone {cone}")}", []);
    }

    private async Task<ToolOutcome> SearchImagesAsync(JsonObject args, SearchRequester requester, CancellationToken cancellationToken)
    {
        if (!features.Value.RecipeImageRetrieval)
            return Failed("search_recipe_images", "Tile search is switched off at the moment.");
        var recipeId = Guid.TryParse(Text(args, "recipeId"), out var id) ? id : (Guid?)null;
        var recipeCode = Text(args, "recipeCode");
        var materials = (args["materials"] as JsonArray ?? [])
            .Select(r => new RawMaterialInput(r?["rawName"]?.GetValue<string>() ?? r?["material"]?.GetValue<string>(), r?["amount"]?.GetValue<decimal>(), r?["unit"]?.GetValue<string>()))
            .Where(r => !string.IsNullOrWhiteSpace(r.RawName)).ToList();
        var umfInput = requester.IsAdmin && args["umf"] is JsonObject u ? JsonSerializer.SerializeToElement(u, Json) : (JsonElement?)null;
        var type = recipeId is not null || recipeCode is not null ? "recipe" : materials.Count > 0 ? "raw_recipe" : umfInput is not null ? "umf" : null;
        if (type is null) return Failed("search_recipe_images", "Give a recipe (id or code), raw materials, or a UMF to search with.");

        var topK = Math.Clamp(args["topK"]?.GetValue<int>() ?? 6, 1, 12);
        var firing = new SearchFiringInput(Dec(args, "coneMin"), Dec(args, "coneMax"), Text(args, "atmosphere"), null);
        var response = await search.SearchAsync(new RecipeImageSearchRequest(
            new SearchQueryInput(type, umfInput, null, null, null, recipeId, recipeCode, null, materials.Count > 0 ? materials : null),
            firing, null, topK, true), requester, null, cancellationToken);

        // The assistant is told only what the user may see, so it can't explain the match by its chemistry either.
        var items = SearchVisibility.ForViewer(response, requester.IsAdmin).Items.Take(topK).ToList();
        var sources = items.Take(3).Select(i => new ChatSourceDto(i.ImageId.ToString(), "image",
            $"Tile #{i.Rank}{(i.Source.RecipeCode is null ? "" : $" · {i.Source.RecipeCode}")}",
            $"{Firing(i.Firing)}{(i.Tile.Surface is null ? "" : $", {i.Tile.Surface}")}{(i.Tile.ColourFamily is null ? "" : $", {i.Tile.ColourFamily}")} · {i.ConfidenceBand} match ({i.Score:0.00})")).ToList();
        var what = type switch { "recipe" => $"recipe {recipeCode ?? "selected"}", "raw_recipe" => "your recipe", _ => "the UMF" };
        return Done("search_recipe_images", new
        {
            searchId = response.SearchId,
            results = items.Select(i => new
            {
                i.Rank, i.ImageId, score = Math.Round(i.Score, 3), confidence = i.ConfidenceBand, recipeCode = i.Source.RecipeCode,
                firing = Firing(i.Firing), i.Tile.Surface, i.Tile.Opacity, i.Tile.ColourFamily, i.MatchedFields, i.Limitations
            }),
            warnings = response.Warnings,
            note = "The app shows these tile photos beside your answer. Refer to them as Tile #rank with the recipe code; do not write image links or URLs."
        }, items.Count == 0 ? $"Searched tiles for {what}; nothing matched closely enough" : $"Found {items.Count} tile photo{(items.Count == 1 ? "" : "s")} matching {what}", sources, response.SearchId);
    }

    private async Task<ToolOutcome> GetRecipeAsync(JsonObject args, SearchRequester requester, CancellationToken cancellationToken)
    {
        var query = db.Recipes.AsNoTracking().Include(x => x.BaseGlaze).Where(x => x.IsActive);
        Recipe? recipe = null;
        if (Guid.TryParse(Text(args, "recipeId"), out var id)) recipe = await query.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        else if (LabCode.NormalizeRecipe(Text(args, "code")) is { } code) recipe = await query.SingleOrDefaultAsync(x => x.Code == code, cancellationToken);
        else if (Text(args, "name") is { } name)
        {
            var needle = name.ToLowerInvariant();
            foreach (var candidate in await query.Where(x => x.Name.ToLower().Contains(needle)).OrderBy(x => x.Name).Take(20).ToListAsync(cancellationToken))
                if (await search.CanUseRecipeAsync(candidate, requester, cancellationToken)) { recipe = candidate; break; }
        }
        if (recipe is null || !await search.CanUseRecipeAsync(recipe, requester, cancellationToken))
            return Failed("get_recipe", "No recipe with that id, code or name is visible to you.");

        var snapshot = await db.RecipeCalculationResults.AsNoTracking().Where(x => x.RecipeId == recipe.Id).OrderByDescending(x => x.CreatedAt).FirstOrDefaultAsync(cancellationToken);
        var materials = MaterialRows(recipe.RecipeJson);
        var label = recipe.Code is null ? recipe.Name : $"{recipe.Code} {recipe.Name}";
        return Done("get_recipe", new
        {
            recipe.Id, recipe.Code, recipe.Name, recipe.Description, baseGlaze = recipe.BaseGlaze?.Code, materials,
            additions = JsonNode.Parse(string.IsNullOrWhiteSpace(recipe.AdditionsJson) ? "[]" : recipe.AdditionsJson),
            recipe.OutcomeNote, umf = snapshot is null || !requester.IsAdmin ? null : JsonNode.Parse(snapshot.UmfJson)
        }, $"Looked up recipe {label}",
        [new ChatSourceDto(recipe.Id.ToString(), "recipe", label, Trim(string.Join(", ", materials.Select(m => $"{m.Name} {m.Amount.ToString("0.##", CultureInfo.InvariantCulture)}")), 240))]);
    }

    private async Task<ToolOutcome> GetUploadsAsync(JsonObject args, SearchRequester requester, CancellationToken cancellationToken)
    {
        var query = db.Uploads.AsNoTracking().Include(x => x.Recipe).Where(x => x.OwnerUserId == requester.UserId);
        if (Guid.TryParse(Text(args, "uploadId"), out var id)) query = query.Where(x => x.Id == id);
        var uploads = await query.OrderByDescending(x => x.CreatedAt).Take(5).ToListAsync(cancellationToken);
        if (uploads.Count == 0) return Done("get_upload_status", new { uploads = Array.Empty<object>() }, "Checked your uploads; you have none yet", []);
        return Done("get_upload_status", new
        {
            uploads = uploads.Select(u => new
            {
                u.Id, status = u.Status.ToString(), fileName = u.OriginalFileName, recipe = u.Recipe?.Code ?? u.Recipe?.Name,
                u.ReviewNote, u.FailureReason, u.CreatedAt, u.ReviewedAt
            })
        }, uploads.Count == 1 ? $"Checked upload {uploads[0].OriginalFileName}: {uploads[0].Status}" : $"Checked your {uploads.Count} most recent uploads", []);
    }

    private ToolOutcome GetMaterial(JsonObject args)
    {
        var name = Text(args, "name");
        if (name is null) return Failed("get_material", "Give a material name.");
        var found = umf.ListMaterials(name, 3, 0).Items;
        if (found.Count == 0) return Done("get_material", new { materials = Array.Empty<object>() }, $"Looked up {name}; it is not in the materials list", []);
        return Done("get_material", new { materials = found.Select(m => new { m.Name, m.Type, loiPct = m.LoiPct, oxideAnalysis = m.OxideAnalysis }) },
            $"Looked up the material {found[0].Name}",
            found.Take(1).Select(m => new ChatSourceDto(m.Id, "material", m.Name, Trim($"{m.Type}; {m.OxideAnalysis.GetRawText()}", 240))).ToList());
    }

    // ---- helpers ----

    private static ToolOutcome Done(string name, object result, string summary, IReadOnlyList<ChatSourceDto> sources, Guid? searchId = null)
    {
        var json = JsonSerializer.Serialize(result, Json);
        return new ToolOutcome(json.Length > MaxResultChars ? json[..MaxResultChars] : json, new ChatToolCallDto(name, summary, searchId), sources);
    }

    private static ToolOutcome Failed(string name, string message) =>
        new(JsonSerializer.Serialize(new { error = message }, Json), new ChatToolCallDto(name, Trim(message, 200)), []);

    private static List<(string Name, double Amount)> MaterialRows(string recipeJson)
    {
        try
        {
            var root = JsonNode.Parse(string.IsNullOrWhiteSpace(recipeJson) ? "{}" : recipeJson) as JsonObject;
            var rows = root?["full"] as JsonArray ?? root?["materials"] as JsonArray ?? root?["rows"] as JsonArray ?? [];
            return rows.Select(r => (r?["rawName"]?.GetValue<string>() ?? r?["material"]?.GetValue<string>() ?? r?["name"]?.GetValue<string>() ?? "?",
                r?["amount"]?.GetValue<double>() ?? 0)).ToList();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { return []; }
    }

    internal static string Firing(SearchFiringSummary f)
    {
        var cone = f.ConeMin is null ? "cone unknown" : f.ConeMin == f.ConeMax || f.ConeMax is null ? $"cone {f.ConeMin:0.#}" : $"cone {f.ConeMin:0.#}–{f.ConeMax:0.#}";
        return f.Atmosphere is null ? cone : $"{cone}, {f.Atmosphere}";
    }

    private static string? Text(JsonObject args, string key) =>
        args[key] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s.Trim()
        : args[key] is JsonValue n && n.TryGetValue<double>(out var d) ? d.ToString(CultureInfo.InvariantCulture) : null;

    private static decimal? Dec(JsonObject args, string key) =>
        args[key] is JsonValue v && (v.TryGetValue<decimal>(out var d) || (v.TryGetValue<string>(out var s) && decimal.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d))) ? d : null;

    private static double Round(double value) => Math.Round(value, 3);
    private static string Trim(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";

    private static JsonObject Schema(JsonObject properties, params string[] required)
    {
        var schema = new JsonObject { ["type"] = "object", ["properties"] = properties };
        if (required.Length > 0) schema["required"] = new JsonArray(required.Select(r => (JsonNode)r).ToArray());
        return schema;
    }

    private static JsonObject Str(string description) => new() { ["type"] = "string", ["description"] = description };
    private static JsonObject Num(string description) => new() { ["type"] = "number", ["description"] = description };
    private static JsonObject ArrayOf(JsonObject items, string description) => new() { ["type"] = "array", ["description"] = description, ["items"] = items };
    private static JsonObject Object(JsonObject properties, params string[] required) => Schema(properties, required);
}
