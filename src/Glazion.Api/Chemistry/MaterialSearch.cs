using System.Text.Json;
using System.Text.RegularExpressions;

namespace Glazion.Api.Chemistry;

public sealed record MaterialCategory(string Key, string Label);
public sealed record CuratedMaterial(string Name, string Category, IReadOnlyList<string> Aliases);

/// <summary>
/// material-catalog.json: categories, the common studio list and the names potters type for them ("EPK", "Whiting",
/// "RIO"). It only shapes the dropdown; the calculator never reads it, so results stay identical to the apps'.
/// </summary>
public sealed class MaterialCuration
{
    public const string FileName = "material-catalog.json";

    public static readonly MaterialCuration Empty = new([], []);

    public IReadOnlyList<MaterialCategory> Categories { get; }
    /// <summary>Material id (slug of the name) → curation.</summary>
    public IReadOnlyDictionary<string, CuratedMaterial> ById { get; }
    /// <summary>Normalised alias → material id, for exact alias lookups ("epk" → "ep-kaolin").</summary>
    public IReadOnlyDictionary<string, string> AliasToId { get; }

    public MaterialCuration(IReadOnlyList<MaterialCategory> categories, IReadOnlyList<CuratedMaterial> common)
    {
        Categories = categories.Count > 0 ? categories : MaterialSearch.DefaultCategories;
        var byId = new Dictionary<string, CuratedMaterial>(StringComparer.Ordinal);
        var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var c in common)
        {
            var id = ChemistryData.Slug(c.Name);
            byId[id] = c;
            foreach (var alias in c.Aliases) aliases.TryAdd(MaterialSearch.Normalize(alias), id);
        }
        ById = byId;
        AliasToId = aliases;
    }

    private sealed record FileShape(List<MaterialCategory>? Categories, List<CuratedMaterial>? Common);

    /// <summary>The file in <paramref name="directory"/>, or <see cref="Empty"/> when there is none.</summary>
    public static MaterialCuration Load(string directory)
    {
        var path = Path.Combine(directory, FileName);
        if (!File.Exists(path)) return Empty;
        var shape = JsonSerializer.Deserialize<FileShape>(File.ReadAllText(path), new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? new FileShape(null, null);
        return new MaterialCuration(shape.Categories ?? [], (shape.Common ?? []).Select(c => c with { Aliases = c.Aliases ?? [] }).ToList());
    }
}

public sealed record MaterialQuery(string? Text, string? Category = null, bool CommonOnly = false, bool WithAnalysisOnly = false);

public sealed record MaterialHit(ChemMaterial Material, string Category, bool Common, IReadOnlyList<string> Aliases, string? MatchedAlias, int Score);

/// <summary>
/// Ranked material search for the dropdown. Each material appears once (the entry the calculator uses). With text, a
/// hit can come from the name or an alias, scored exact &gt; prefix &gt; word prefix &gt; contains &gt; all words &gt;
/// one-typo; common materials and materials with an analysis rank higher. Without text, the list keeps file order
/// (common-only lists are grouped by category).
/// </summary>
public static partial class MaterialSearch
{
    public static readonly IReadOnlyList<MaterialCategory> DefaultCategories =
    [
        new("silica", "Silica"), new("clay", "Clays & kaolins"), new("feldspar", "Feldspars & lithium minerals"), new("flux", "Fluxes & melters"),
        new("frit", "Frits"), new("opacifier", "Opacifiers"), new("colorant", "Colorants: metallic oxides"), new("stain", "Colorants: stains"), new("ash", "Ashes"),
        new("additive", "Additives (no chemistry)"), new("other", "Other")
    ];

    private const int Exact = 1000, Prefix = 800, WordPrefix = 600, Contains = 400, AllWords = 300, Fuzzy = 150;
    private const int CommonBoost = 120, AnalysisBoost = 40;

    public static IReadOnlyList<MaterialHit> Search(ChemistryData data, MaterialCuration curation, MaterialQuery query)
    {
        var q = Normalize(query.Text ?? string.Empty);
        var qTokens = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var hits = new List<MaterialHit>();
        foreach (var m in data.Materials)
        {
            if (!ReferenceEquals(data.MaterialsById[m.Id], m)) continue;               // one entry per id
            curation.ById.TryGetValue(m.Id, out var curated);
            var category = CategoryOf(m, curation);
            var common = curated is not null;
            if (query.Category is { } wanted && !string.Equals(category, wanted, StringComparison.Ordinal)) continue;
            if (query.CommonOnly && !common) continue;
            if (query.WithAnalysisOnly && m.OxideAnalysis.Count == 0) continue;
            var aliases = curated?.Aliases ?? [];

            if (q.Length == 0) { hits.Add(new MaterialHit(m, category, common, aliases, null, 0)); continue; }

            var nameScore = Score(Normalize(m.Name), q, qTokens);
            var score = nameScore;
            string? matchedAlias = null;
            foreach (var alias in aliases)
            {
                var s = Score(Normalize(alias), q, qTokens) - 1;                         // the real name wins ties
                if (s > score) { score = s; matchedAlias = alias; }
            }
            if (score <= 0) continue;
            // Only name the alias when the name itself does not visibly match ("EPK" → EP Kaolin, not "custer" → Custer Feldspar).
            if (nameScore >= WordPrefix) matchedAlias = null;
            score += (common ? CommonBoost : 0) + (m.OxideAnalysis.Count > 0 ? AnalysisBoost : 0);
            hits.Add(new MaterialHit(m, category, common, aliases, matchedAlias, score));
        }

        if (q.Length > 0)
            return hits.OrderByDescending(h => h.Score).ThenBy(h => h.Material.Name.Length).ThenBy(h => h.Material.Name, StringComparer.OrdinalIgnoreCase).ToList();
        if (query.CommonOnly)
        {
            var order = curationOrder(curation);
            return hits.OrderBy(h => order.GetValueOrDefault(h.Category, int.MaxValue)).ThenBy(h => h.Material.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
        return hits;

        static Dictionary<string, int> curationOrder(MaterialCuration c) => c.Categories.Select((x, i) => (x.Key, i)).ToDictionary(x => x.Key, x => x.i);
    }

    private static int Score(string candidate, string q, string[] qTokens)
    {
        if (candidate.Length == 0) return 0;
        if (candidate == q) return Exact;
        if (candidate.StartsWith(q, StringComparison.Ordinal)) return Prefix;
        var words = candidate.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (qTokens.All(t => words.Any(w => w.StartsWith(t, StringComparison.Ordinal)))) return WordPrefix;
        if (candidate.Contains(q, StringComparison.Ordinal)) return Contains;
        if (qTokens.All(t => candidate.Contains(t, StringComparison.Ordinal))) return AllWords;
        // One typo per word of four letters or more ("nephaline", "wolastonite"); short tokens must match exactly.
        if (qTokens.All(t => t.Length >= 4 ? words.Any(w => WithinOneEdit(t, w) || (w.Length > t.Length && WithinOneEdit(t, w[..t.Length]))) : words.Any(w => w.StartsWith(t, StringComparison.Ordinal))))
            return Fuzzy;
        return 0;
    }

    /// <summary>Lower case, "#", "-" and other punctuation become spaces, runs collapse: "OM #4 Ball-Clay" → "om 4 ball clay".</summary>
    public static string Normalize(string value) => Spaces().Replace(NonWord().Replace(value.ToLowerInvariant(), " "), " ").Trim();

    [GeneratedRegex(@"[^\p{L}\p{N}]+")] private static partial Regex NonWord();
    [GeneratedRegex(@"\s+")] private static partial Regex Spaces();

    private static bool WithinOneEdit(string a, string b)
    {
        if (Math.Abs(a.Length - b.Length) > 1) return false;
        int i = 0, j = 0, edits = 0;
        while (i < a.Length && j < b.Length)
        {
            if (a[i] == b[j]) { i++; j++; continue; }
            if (++edits > 1) return false;
            if (a.Length > b.Length) i++;
            else if (b.Length > a.Length) j++;
            else if (i + 1 < a.Length && j + 1 < b.Length && a[i] == b[j + 1] && a[i + 1] == b[j]) { i += 2; j += 2; }   // swapped letters
            else { i++; j++; }
        }
        return edits + (a.Length - i) + (b.Length - j) <= 1;
    }

    private static readonly string[] ColorantOxides = ["Fe2O3", "FeO", "CoO", "CuO", "Cr2O3", "MnO", "MnO2", "NiO", "V2O5", "Cu2O"];
    private static readonly string[] OpacifierOxides = ["ZrO2", "ZrO", "SnO2", "TiO2"];

    /// <summary>The catalog's category when it lists the material, else <see cref="Categorize"/>.</summary>
    public static string CategoryOf(ChemMaterial m, MaterialCuration curation) =>
        curation.ById.TryGetValue(m.Id, out var curated) ? curated.Category : Categorize(m);

    /// <summary>Category from the name, then from the dominant oxide, for materials the catalog does not list.</summary>
    public static string Categorize(ChemMaterial m)
    {
        var n = " " + Normalize(m.Name) + " ";
        bool Word(params string[] words) => words.Any(w => n.Contains(" " + w + " ", StringComparison.Ordinal));
        bool Part(params string[] parts) => parts.Any(p => n.Contains(p, StringComparison.Ordinal));
        if (Part(" stain") || n.StartsWith(" mason ", StringComparison.Ordinal)) return "stain";
        if (Part("frit")) return "frit";
        if (Word("ash") && !Part(" bone ash ", " pearl ash ", " soda ash ")) return "ash";
        if (Part("kaolin", "clay", "fireclay", "bentonite", "halloysite", "stoneware", "earthenware") || Word("ball", "slip")) return "clay";
        if (Part("feldsp", "feldesp", "spar", "nepheline", "petalite", "spodumene", "lepidolite", "aplite", "pegmatite", "albite", "cornwall stone", "cornish stone")) return "feldspar";
        if (Word("silica", "quartz", "flint", "sand")) return "silica";
        if (m.OxideAnalysis.Count == 0) return "additive";
        var (oxide, value) = m.OxideAnalysis.Select(a => (a.Key, Value: a.Value.ValueKind == JsonValueKind.Number ? a.Value.GetDouble() : 0)).MaxBy(a => a.Value);
        if (value >= 20 && ColorantOxides.Contains(oxide)) return "colorant";
        if (value >= 40 && OpacifierOxides.Contains(oxide)) return "opacifier";
        return "flux";
    }
}
