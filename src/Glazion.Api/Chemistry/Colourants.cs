using System.Text.Json;
using Glazion.Api.Domain;

namespace Glazion.Api.Chemistry;

/// <summary>A material the UMF cannot see (no oxide analysis, e.g. a Mason stain), with its amount per 100 parts of the analysed base.</summary>
public sealed record Colourant(string MaterialId, string Name, double PercentOfBase);

/// <summary>
/// A colorant as potters name it, one of two kinds (27 Oct 2026 meeting: colorants come from metallic oxides and from
/// stains): Kind is "metallic_oxide" or "stain". PercentOfBase is parts per 100 parts of the rest of the recipe.
/// </summary>
public sealed record ColorantMaterial(string MaterialId, string Name, string Kind, double PercentOfBase);

/// <summary>
/// Stains and other materials with no oxide analysis (in the shipped list 845 of 2,830, of which 333 are stains; the
/// rest are data gaps such as Marblewhite Whiting) add nothing to a UMF, so "base + 2% Shell Pink" and
/// "base + 10% Alpine Rose" have exactly the same chemistry as the base. The colourant mix keeps what the UMF loses:
/// which stains (and colorants with no analysis) a recipe adds and how much, as parts per 100 parts of the materials
/// that do have an analysis (the way potters write additions: base 100 + 2.5 stain → 2.5). A data gap is not a
/// colourant: it is reported as a material with no analysis, but adds no colour to compare.
/// </summary>
public static class Colourants
{
    /// <summary>v2: only stains and colorants count; v1 counted every material with no analysis.</summary>
    public const string Version = "colourants-v2";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static bool HasAnalysis(ChemMaterial material) => material.OxideAnalysis.Any(kv => GlazeChemistry.ToNumber(kv.Value) is > 0);

    /// <summary>A material with no analysis that colours: a stain, or a colorant whose analysis is missing.</summary>
    private static bool Colours(ChemMaterial material, MaterialCuration curation) => MaterialSearch.CategoryOf(material, curation) is "stain" or "colorant";

    /// <summary>From calculator lines (material id + share of the batch). Empty when the recipe has no such material.</summary>
    public static IReadOnlyList<Colourant> FromLines(IReadOnlyList<RecipeLine> lines, ChemistryData data, MaterialCuration curation)
    {
        double analysed = 0;
        var unanalysed = new Dictionary<string, (string Name, double Parts)>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            if (!data.MaterialsById.TryGetValue(line.MaterialId, out var material) || line.PartsPct <= 0) continue;
            if (HasAnalysis(material)) { analysed += line.PartsPct; continue; }
            if (!Colours(material, curation)) continue;
            var current = unanalysed.GetValueOrDefault(line.MaterialId, (material.Name, 0));
            unanalysed[line.MaterialId] = (current.Name, current.Parts + line.PartsPct);
        }
        if (analysed <= 0 || unanalysed.Count == 0) return [];
        return unanalysed.OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => new Colourant(kv.Key, kv.Value.Name, Math.Round(kv.Value.Parts / analysed * 100, 4)))
            .ToList();
    }

    /// <summary>Names of every material in the lines with no oxide analysis, stain or not: the UMF leaves them all out.</summary>
    public static IReadOnlyList<string> WithoutAnalysis(IReadOnlyList<RecipeLine> lines, ChemistryData data) =>
        lines.Where(l => l.PartsPct > 0).Select(l => data.MaterialsById.GetValueOrDefault(l.MaterialId)).OfType<ChemMaterial>()
            .Where(m => !HasAnalysis(m)).Select(m => m.Name).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>
    /// Every colorant in a recipe, both kinds, by the material's category (catalog, else name and analysis), each per
    /// 100 parts of everything else. Unlike <see cref="FromLines"/>, which keeps only what the UMF cannot see for ranking,
    /// this is what a person reads: "3% Cobalt Carbonate (metallic oxide), 2% Shell Pink (stain)".
    /// </summary>
    public static IReadOnlyList<ColorantMaterial> MaterialsFromLines(IReadOnlyList<RecipeLine> lines, ChemistryData data, MaterialCuration curation)
    {
        double rest = 0;
        var found = new Dictionary<string, (string Name, string Kind, double Parts)>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            if (!data.MaterialsById.TryGetValue(line.MaterialId, out var material) || line.PartsPct <= 0) continue;
            var kind = MaterialSearch.CategoryOf(material, curation) switch { "colorant" => "metallic_oxide", "stain" => "stain", _ => null };
            if (kind is null) { rest += line.PartsPct; continue; }
            var current = found.GetValueOrDefault(line.MaterialId, (material.Name, kind, 0));
            found[line.MaterialId] = (current.Name, kind, current.Parts + line.PartsPct);
        }
        if (rest <= 0 || found.Count == 0) return [];
        return found.OrderByDescending(kv => kv.Value.Parts).ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => new ColorantMaterial(kv.Key, kv.Value.Name, kv.Value.Kind, Math.Round(kv.Value.Parts / rest * 100, 4)))
            .ToList();
    }

    /// <summary>Lines recorded by the calculator on a snapshot (<c>InputJson.lines</c>); null when it recorded none (a supplied UMF).</summary>
    public static IReadOnlyList<RecipeLine>? LinesFromSnapshot(RecipeCalculationResult? snapshot)
    {
        if (snapshot is null || string.IsNullOrWhiteSpace(snapshot.InputJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(snapshot.InputJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("lines", out var lines) || lines.ValueKind != JsonValueKind.Array) return null;
            var result = lines.Deserialize<List<RecipeLine>>(Json);
            return result is { Count: > 0 } ? result : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// The colourant mix of an image: from its calculation snapshot's lines, else from its primary recipe's material
    /// rows (names a potter uses, such as "Shell Pink 6000", count as the material they name). Null when neither is
    /// known (the comparison is then left out rather than guessed).
    /// </summary>
    public static IReadOnlyList<Colourant>? ForAsset(ImageAsset asset, ChemistryData data, MaterialCuration curation)
    {
        var lines = LinesFromSnapshot(asset.CalculationResult);
        if (lines is null)
        {
            var recipe = asset.Recipes.OrderByDescending(x => x.Verified).ThenBy(x => x.CreatedAt).Select(x => x.Recipe).FirstOrDefault(x => x is not null);
            if (recipe is null) return null;
            try { lines = GlazeChemistry.CalculateRecipe(UmfCalculationService.WithAliases(UmfCalculationService.RowsFromRecipe(recipe), data, curation), data)?.Lines; }
            catch (Contracts.ApiProblemException) { return null; } // the recipe stores no material rows
        }
        return lines is null ? null : FromLines(lines, data, curation);
    }

    public static string Describe(IReadOnlyList<Colourant> mix) => mix.Count == 0 ? "no stain" : string.Join(" + ", mix.Select(x => $"{x.PercentOfBase.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}% {x.Name}"));

    /// <summary>Search warning for a query material with no oxide analysis: the UMF leaves it out (intended for a stain, a data gap for, say, a whiting).</summary>
    public static string NoAnalysisWarning(string materialName) => "no_oxide_analysis:" + materialName;
}
