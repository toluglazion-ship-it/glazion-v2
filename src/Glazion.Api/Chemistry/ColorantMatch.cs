using System.Globalization;
using System.Text.Json;

namespace Glazion.Api.Chemistry;

/// <summary>
/// The colorants of one glaze, both kinds: metallic oxides as UMF amounts (iron counts FeO as Fe2O3), and stains by
/// material and amount. Stains is null when they are unknown (a UMF typed by hand, or a tile with no recorded
/// materials); Oxides is never null, since every glaze compared has a UMF.
/// </summary>
public sealed record ColorantProfile(IReadOnlyDictionary<string, double> Oxides, IReadOnlyList<Colourant>? Stains);

/// <summary>
/// One comparison for both kinds of colorant (27 Oct 2026 meeting). Before this, only stains were compared on their
/// own; metallic oxides sat inside the whole-UMF comparison, where 0.03 of cobalt is lost next to 3 of silica, so a
/// copper green ranked close to an iron recipe.
///
/// Each amount is counted in tints: the amount that gives a light but clear colour in a typical glaze, so 0.1% cobalt
/// weighs as much as 1% iron. Less than a quarter of a tint counts as none, which ignores the iron and titanium that
/// clays and feldspars carry. The two glazes' tints are then compared as overlap, with one tint of slack:
/// (Σ min + 1) / (Σ max + 1). The same colorants in the same amounts → 1; neither has any → 1; 1.5% iron against a
/// copper green → about 0.2.
/// </summary>
public static class ColorantMatch
{
    public const string Version = "colorant-match-v1";

    /// <summary>
    /// Oxide, its plain name, and one tint as a UMF amount: the weight % in brackets, in a glaze of about 280 g per unity
    /// formula (a typical stoneware glaze).
    /// </summary>
    public static readonly IReadOnlyList<(string Oxide, string Name, double Tint)> Oxides =
    [
        ("Fe2O3", "iron", 0.018),       // 1% Fe2O3
        ("CoO", "cobalt", 0.004),       // 0.1% CoO
        ("CuO", "copper", 0.018),       // 0.5% CuO
        ("Cr2O3", "chrome", 0.004),     // 0.2% Cr2O3
        ("MnO", "manganese", 0.04),     // 1% MnO
        ("NiO", "nickel", 0.02),        // 0.5% NiO
        ("V2O5", "vanadium", 0.03),     // 2% V2O5
        ("TiO2", "titanium", 0.1)       // 3% TiO2, as rutile
    ];

    /// <summary>One tint of a stain: 1 part per 100 parts of the base.</summary>
    public const double StainTint = 1.0;
    private const double Trace = 0.25;
    private const double Slack = 1.0;

    /// <summary>The metallic oxides of a UMF as sent or stored (FeO, V2O5 and the rest included); missing ones are 0.</summary>
    public static IReadOnlyDictionary<string, double> OxidesOf(JsonElement umf)
    {
        double Read(string oxide) => umf.ValueKind == JsonValueKind.Object && umf.TryGetProperty(oxide, out var v) && v.ValueKind == JsonValueKind.Number && v.GetDouble() is var d && d > 0 ? d : 0;
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (oxide, _, _) in Oxides)
        {
            // Two FeO carry the iron of one Fe2O3.
            var value = oxide == "Fe2O3" ? Read("Fe2O3") + Read("FeO") / 2 : Read(oxide);
            if (value > 0) result[oxide] = value;
        }
        return result;
    }

    public static IReadOnlyDictionary<string, double> OxidesOf(string umfJson)
    {
        using var document = JsonDocument.Parse(umfJson);
        return OxidesOf(document.RootElement);
    }

    /// <summary>Tints per oxide and per stain ("stain:id"), each less a trace. Stains only when both sides know them.</summary>
    private static Dictionary<string, double> Tints(ColorantProfile profile, bool withStains)
    {
        var tints = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (oxide, _, tint) in Oxides)
            if (profile.Oxides.TryGetValue(oxide, out var value) && value / tint - Trace is > 0 and var t) tints[oxide] = t;
        if (withStains && profile.Stains is { } stains)
            foreach (var stain in stains)
                if (stain.PercentOfBase / StainTint - Trace is > 0 and var t) tints["stain:" + stain.MaterialId] = tints.GetValueOrDefault("stain:" + stain.MaterialId) + t;
        return tints;
    }

    /// <summary>Whether stains take part: only when both glazes' stains are known.</summary>
    public static bool ComparesStains(ColorantProfile query, ColorantProfile candidate) => query.Stains is not null && candidate.Stains is not null;

    public static double Similarity(ColorantProfile query, ColorantProfile candidate)
    {
        var withStains = ComparesStains(query, candidate);
        var q = Tints(query, withStains);
        var c = Tints(candidate, withStains);
        double min = 0, max = 0;
        foreach (var key in q.Keys.Union(c.Keys))
        {
            var a = q.GetValueOrDefault(key);
            var b = c.GetValueOrDefault(key);
            min += Math.Min(a, b);
            max += Math.Max(a, b);
        }
        return (min + Slack) / (max + Slack);
    }

    /// <summary>Whether the glaze has any colorant above a trace.</summary>
    public static bool HasAny(ColorantProfile profile) => Tints(profile, withStains: true).Count > 0;

    /// <summary>"iron (Fe2O3 0.027) and 2% Stain 6000", or "no colorants". Stains are listed only when <paramref name="withStains"/>.</summary>
    public static string Describe(ColorantProfile profile, bool withStains)
    {
        var parts = new List<string>();
        foreach (var (oxide, name, tint) in Oxides)
            if (profile.Oxides.TryGetValue(oxide, out var value) && value / tint > Trace)
                parts.Add($"{name} ({oxide} {value.ToString("0.###", CultureInfo.InvariantCulture)})");
        if (withStains && profile.Stains is { } stains)
            parts.AddRange(stains.Where(s => s.PercentOfBase / StainTint > Trace).Select(s => $"{s.PercentOfBase.ToString("0.###", CultureInfo.InvariantCulture)}% {s.Name}"));
        return parts.Count switch
        {
            0 => "no colorants",
            1 => parts[0],
            _ => string.Join(", ", parts[..^1]) + " and " + parts[^1]
        };
    }
}
