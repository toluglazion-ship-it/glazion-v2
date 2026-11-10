using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Glazion.Api.Chemistry;

/// <summary>A typed row. materialId (from the dropdown) wins over material: it resolves to the name of the entry the
/// calculator uses for that id, which then goes through the same name lookup (handoff API.md).</summary>
public sealed record RecipeRowInput(string? Material, JsonElement? Amount, string? MaterialId = null);
public sealed record RecipeLine(string MaterialId, double PartsPct);

/// <summary>Ratios as the apps read them; a ratio that can't be worked out is left out of the JSON.</summary>
public sealed record ChemRatios(
    [property: JsonPropertyName("si_al"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? SiAl,
    [property: JsonPropertyName("alkali_over_alkaline"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? AlkaliOverAlkaline,
    [property: JsonPropertyName("b2o3_pct")] double B2O3Pct);

public sealed record ChemResult(
    OrderedDictionary<string, double> Moles,
    OrderedDictionary<string, double> MolePct,
    OrderedDictionary<string, double> AnalysisPct,
    OrderedDictionary<string, double> Umf,
    double FluxSum,
    ChemRatios Ratios,
    double Cte,
    IReadOnlyList<string> Warnings);

public sealed record RecipeResult(ChemResult Chem, IReadOnlyList<RecipeLine> Lines);

/// <summary>
/// Why a row did not reach the calculation, by its index in the request. Reasons are the apps' wording (handoff
/// API.md): "no material", "no amount", "unknown material", "amount is not a number".
/// </summary>
public sealed record SkippedRow(int Index, string? Material, string Reason);

/// <summary>
/// Port of the client's packages/chemistry (calculateRecipe in recipe.ts, computeChemistry in chemistry.ts), as
/// described in "Glazion UMF Calculator: How It Works" (2026-10-04). It reproduces today's agreed behaviour exactly,
/// including the known issues, so the backend passes the 40 fixtures and matches the browser during the switch.
/// Fixes are separate, versioned changes (bump <see cref="CalculationVersion"/>).
/// </summary>
public static partial class GlazeChemistry
{
    public const string CalculationProfile = "glazion-umf-v1";
    public const string CalculationVersion = "glazion-chemistry-1.0.0";
    public const double Zero = 1e-12;

    /// <summary>Names the apps use for this same calculation ("flux-to-1" is their unityMode).</summary>
    public static readonly IReadOnlySet<string> ProfileAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "flux-to-1", "flux_to_1" };

    /// <summary>DEFAULT_FLUX_SET. PbO, FeO, MnO, CoO, NiO and CuO are not in it (known issue, client decision).</summary>
    public static readonly string[] FluxSet = ["K2O", "Na2O", "Li2O", "CaO", "MgO", "BaO", "SrO", "ZnO"];
    private static readonly string[] Alkali = ["K2O", "Na2O", "Li2O"];
    private static readonly string[] AlkalineEarth = ["CaO", "MgO", "BaO", "SrO", "ZnO"];

    /// <summary>estimateCTE coefficients in chemistry.ts declaration order, applied to UMF values (known issue: wrong scale, client decision).</summary>
    public static readonly (string Oxide, double Coefficient)[] CteTable =
    [
        ("SiO2", 0.5), ("Al2O3", -2.0), ("B2O3", -0.8), ("Li2O", 35.0), ("Na2O", 40.0), ("K2O", 38.0), ("MgO", 5.0),
        ("CaO", 13.0), ("SrO", 11.0), ("BaO", 10.0), ("ZnO", 7.0), ("PbO", 11.0), ("TiO2", 8.0), ("P2O5", 5.0), ("ZrO2", 4.0)
    ];

    /// <summary>UMF_GROUPS display layout. MoO3, WO3, Nb2O5 and Ta2O5 sit in no group.</summary>
    public static readonly IReadOnlyList<(string Key, string Label, string[] Oxides)> UmfGroups =
    [
        ("r2o", "Fluxes, R₂O", ["Na2O", "K2O", "Li2O"]),
        ("ro", "Fluxes, RO", ["CaO", "MgO", "BaO", "SrO", "ZnO", "PbO", "FeO", "MnO", "CoO", "NiO", "CuO"]),
        ("r2o3", "Stabilisers, R₂O₃", ["Al2O3", "Fe2O3", "Cr2O3", "V2O5", "La2O3", "Y2O3"]),
        ("ro2", "Glass formers, RO₂", ["SiO2", "B2O3", "ZrO2", "TiO2", "SnO2", "P2O5", "CeO2"])
    ];

    /// <summary>The 31 oxide symbols the calculator knows (oxides.ts), used where only the symbol set is needed.</summary>
    public static readonly IReadOnlySet<string> KnownOxideSymbols = new HashSet<string>(UmfGroups.SelectMany(g => g.Oxides).Concat(["MoO3", "WO3", "Nb2O5", "Ta2O5"]), StringComparer.Ordinal);

    public static string OxideSumWarning(string name) => $"Material {name} oxide sum > 100%";
    public static string UnknownOxideWarning(string symbol, string name) => $"Unknown oxide {symbol} in material {name}";
    public const string NoFluxWarning = "UMF undefined: flux sum is zero. Add flux oxides.";

    /// <summary>calculateRecipe(rows, materials, cone). Returns null when nothing usable remains. The cone only affects target ranges.</summary>
    public static RecipeResult? CalculateRecipe(IReadOnlyList<RecipeRowInput> rows, ChemistryData data, List<SkippedRow>? skipped = null)
    {
        if (data.Materials.Count == 0) return null;
        var lines = new List<(string Id, double Amount)>();
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var byId = !string.IsNullOrEmpty(row.MaterialId);
            var name = byId ? (data.MaterialsById.TryGetValue(row.MaterialId!, out var picked) ? picked.Name : null) : row.Material;
            if (!byId && string.IsNullOrEmpty(name)) { skipped?.Add(new(i, row.Material, "no material")); continue; }
            if (IsEmptyAmount(row.Amount)) { skipped?.Add(new(i, name ?? row.MaterialId, "no amount")); continue; }
            var material = name is null ? null : FindByName(data, name);
            if (material is null) { skipped?.Add(new(i, name ?? row.MaterialId, "unknown material")); continue; }
            var amount = ParseFloat(row.Amount!.Value);
            if (!double.IsFinite(amount)) { skipped?.Add(new(i, material.Name, "amount is not a number")); continue; }
            lines.Add((material.Id, amount)); // negative amounts are not rejected here (the apps' inputs stop them)
        }
        if (lines.Count == 0) return null;
        var total = 0.0;
        foreach (var l in lines) total += l.Amount;
        if (total <= 0) return null;

        var scaled = lines.Select(l => new RecipeLine(l.Id, l.Amount / total * 100)).ToList();
        return new RecipeResult(ComputeChemistry(scaled, data), scaled);
    }

    /// <summary>computeChemistry: the core arithmetic, per 100 g of batch, in moles.</summary>
    public static ChemResult ComputeChemistry(IReadOnlyList<RecipeLine> lines, ChemistryData data)
    {
        var warnings = new List<string>();
        var moles = new OrderedDictionary<string, double>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            if (!data.MaterialsById.TryGetValue(line.MaterialId, out var material)) continue;
            var sum = 0.0;
            foreach (var (_, value) in material.OxideAnalysis) sum += ToNumber(value);
            if (sum > 100 + 1e-6) warnings.Add(OxideSumWarning(material.Name));
            foreach (var (symbol, value) in material.OxideAnalysis)
            {
                if (!data.MolecularWeights.TryGetValue(symbol, out var mw)) { warnings.Add(UnknownOxideWarning(symbol, material.Name)); continue; }
                var mass = line.PartsPct * (ToNumber(value) / 100); // grams per 100 g batch, same operation order as chemistry.ts
                moles[symbol] = moles.GetValueOrDefault(symbol) + mass / mw;
            }
        }

        var totalMoles = 0.0;
        foreach (var (_, n) in moles) totalMoles += n;
        var molePct = new OrderedDictionary<string, double>(StringComparer.Ordinal);
        if (totalMoles > Zero) foreach (var (ox, n) in moles) molePct[ox] = 100 * n / totalMoles;

        var grams = new OrderedDictionary<string, double>(StringComparer.Ordinal);
        var totalGrams = 0.0;
        foreach (var (ox, n) in moles) { var g = n * data.MolecularWeights[ox]; grams[ox] = g; totalGrams += g; }
        var analysisPct = new OrderedDictionary<string, double>(StringComparer.Ordinal);
        if (totalGrams > Zero) foreach (var (ox, g) in grams) analysisPct[ox] = 100 * g / totalGrams;

        var fluxSum = 0.0;
        foreach (var f in FluxSet) fluxSum += moles.GetValueOrDefault(f);
        var umf = new OrderedDictionary<string, double>(StringComparer.Ordinal);
        if (fluxSum > Zero) foreach (var (ox, n) in moles) umf[ox] = n / fluxSum;
        else warnings.Add(NoFluxWarning);

        double? siAl = null;
        var si = umf.GetValueOrDefault("SiO2");
        var al = umf.GetValueOrDefault("Al2O3");
        if (si > Zero && al > Zero) siAl = si / al;
        double top = 0, bottom = 0;
        foreach (var o in Alkali) top += umf.GetValueOrDefault(o);
        foreach (var o in AlkalineEarth) bottom += umf.GetValueOrDefault(o);
        double? alkali = bottom > Zero ? top / bottom : null;
        var b2o3 = analysisPct.GetValueOrDefault("B2O3");

        // estimateCTE walks its own coefficient table in declaration order.
        var cte = 0.0;
        foreach (var (ox, coef) in CteTable) cte += umf.GetValueOrDefault(ox) * coef;
        cte = Math.Max(0, cte);

        return new ChemResult(moles, molePct, analysisPct, umf, fluxSum, new ChemRatios(siAl, alkali, b2o3), cte, warnings);
    }

    /// <summary>Name match ignoring case and surrounding spaces; the first match in list order wins.</summary>
    public static ChemMaterial? FindByName(ChemistryData data, string name)
    {
        var key = name.Trim().ToLowerInvariant();
        foreach (var m in data.Materials) if (m.Name.Trim().ToLowerInvariant() == key) return m;
        return null;
    }

    private static bool IsEmptyAmount(JsonElement? amount) =>
        amount is null || amount.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined || (amount.Value.ValueKind == JsonValueKind.String && amount.Value.GetString()!.Length == 0);

    [GeneratedRegex(@"^[+-]?(Infinity|(\d+\.?\d*|\.\d+)([eE][+-]?\d+)?)")] private static partial Regex FloatPrefix();

    /// <summary>JavaScript parseFloat: leading whitespace ignored, longest numeric prefix read ("12g" → 12), otherwise NaN.</summary>
    public static double ParseFloat(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number) return value.GetDouble();
        var text = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString()!,
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => string.Empty
        };
        return ParseFloat(text);
    }

    public static double ParseFloat(string text)
    {
        var match = FloatPrefix().Match(text.TrimStart());
        if (!match.Success) return double.NaN;
        var s = match.Value;
        if (s.EndsWith("Infinity", StringComparison.Ordinal)) return s.StartsWith('-') ? double.NegativeInfinity : double.PositiveInfinity;
        return double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    /// <summary>JavaScript Number() coercion, which is what arithmetic on an analysis value does.</summary>
    public static double ToNumber(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number => value.GetDouble(),
        JsonValueKind.Null => 0,
        JsonValueKind.True => 1,
        JsonValueKind.False => 0,
        JsonValueKind.String => string.IsNullOrWhiteSpace(value.GetString()) ? 0
            : double.TryParse(value.GetString()!.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : double.NaN,
        _ => double.NaN
    };

    // ---- Helpers the apps run around the result (recipe.ts) ----

    /// <summary>Cone as written by the apps: below 1 with a leading zero ("06", "05.5"). The search API sends those as negatives.</summary>
    public static string NormaliseCone(string? cone)
    {
        if (string.IsNullOrWhiteSpace(cone)) return string.Empty;
        var c = cone.Trim();
        if (double.TryParse(c, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && n < 0) return "0" + (-n).ToString(CultureInfo.InvariantCulture);
        return c;
    }

    /// <summary>limitsForCone: rows of glaze-limits.json for the cone, in file order.</summary>
    public static IReadOnlyList<GlazeLimit> LimitsForCone(ChemistryData data, string? cone)
    {
        var key = NormaliseCone(cone);
        return data.Limits.Where(l => l.Cone == key).ToList();
    }

    /// <summary>Field names match OptimisationSuggestion in recipe.ts; minUmf, maxUmf and notes are additive.</summary>
    public sealed record SuggestionMaterial(string Name, double OxideContent, bool InRecipe);
    public sealed record Suggestion(string Oxide, double Current, string Target, string Direction, IReadOnlyList<SuggestionMaterial> Materials, double MinUmf, double MaxUmf, string? Notes);

    /// <summary>suggestOptimisations: for each out-of-range oxide, the top 3 materials with more than 5% of it.</summary>
    public static IReadOnlyList<Suggestion> SuggestOptimisations(ChemistryData data, RecipeResult? result, string? cone)
    {
        var suggestions = new List<Suggestion>();
        var inRecipe = result?.Lines.Select(l => l.MaterialId).ToHashSet(StringComparer.Ordinal) ?? [];
        foreach (var limit in LimitsForCone(data, cone))
        {
            var value = result?.Chem.Umf.GetValueOrDefault(limit.Oxide) ?? 0;
            string direction;
            if (value < limit.MinUmf) direction = "increase";
            else if (value > limit.MaxUmf) direction = "decrease";
            else continue;
            var candidates = data.Materials
                .Select(m => (m, pct: m.OxideAnalysis.Where(a => a.Key == limit.Oxide).Select(a => ToNumber(a.Value)).DefaultIfEmpty(0).First()))
                .Where(x => x.pct > 5)
                .OrderByDescending(x => x.pct) // stable: list order on ties
                .Take(3)
                .Select(x => new SuggestionMaterial(x.m.Name, x.pct, inRecipe.Contains(x.m.Id)))
                .ToList();
            if (candidates.Count == 0) continue;
            suggestions.Add(new Suggestion(limit.Oxide, value,
                $"{limit.MinUmf.ToString("0.00", CultureInfo.InvariantCulture)} - {limit.MaxUmf.ToString("0.00", CultureInfo.InvariantCulture)}", direction, candidates, limit.MinUmf, limit.MaxUmf, limit.Notes));
        }
        return suggestions;
    }

    public sealed record DisplayGroup(string Key, string Label, IReadOnlyList<KeyValuePair<string, double>> Oxides);

    /// <summary>UMF_GROUPS layout: only oxides above 0, largest first.</summary>
    public static IReadOnlyList<DisplayGroup> DisplayGroups(ChemResult chem) =>
        UmfGroups.Select(g => new DisplayGroup(g.Key, g.Label,
            g.Oxides.Where(o => chem.Umf.TryGetValue(o, out var v) && v > 0).Select(o => new KeyValuePair<string, double>(o, chem.Umf[o])).OrderByDescending(x => x.Value).ToList())).ToList();

    public static string CteBand(double cte) => cte < 60 ? "low" : cte < 70 ? "medium" : "high";

    /// <summary>clayBodyNote, verbatim. Known issue: crazing and shivering are reversed (a high-expansion glaze crazes); client decision.</summary>
    public static string ClayBodyNote(double cte) =>
        cte < 60 ? "Low CTE: Fits tight, may craze on high-expansion bodies"
        : cte < 70 ? "Medium CTE: Good fit for most mid-range clay bodies"
        : "High CTE: May shiver on low-expansion bodies";
}
