using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Glazion.Api.Infrastructure;

namespace Glazion.Api.Indexing;

public sealed record CanonicalTextInput(
    string? CalculationProfile,
    IReadOnlyDictionary<string, decimal>? Umf,
    decimal? ConeMin,
    decimal? ConeMax,
    string? Atmosphere,
    string TileVariantKey,
    IReadOnlyDictionary<string, string> Labels);

/// <summary>
/// The stable text document used for text embeddings and as a deterministic audit representation (plan §11.2).
/// It is built from controlled fields only: never user names, capture codes, signed URLs, or unapproved notes.
/// Same input always yields byte-identical text, so the hash doubles as a change detector for re-indexing.
/// </summary>
public static class CanonicalTextBuilder
{
    public const string Version = "canonical-text-v1";
    private static readonly string[] StabilizerOxides = ["Al2O3"];
    private static readonly string[] GlassFormerOxides = ["SiO2"];
    private static readonly string[] OpacifierOxides = ["TiO2", "ZrO2", "SnO2"];
    private static readonly string[] ColourantOxides = ["Fe2O3", "Cr2O3", "CuO", "CoO", "MnO", "NiO"];
    private static readonly string[] OtherOxides = ["P2O5"];
    /// <summary>Controlled label types in emission order (plan §5.5). Keys match LabelsJson (camelCase).</summary>
    private static readonly (string Key, string Line)[] LabelLines = [("surface", "surface"), ("opacity", "opacity"), ("colourFamily", "colour_family"), ("texture", "texture"), ("materialFamily", "material_family")];

    public static string Build(CanonicalTextInput input)
    {
        var sb = new StringBuilder();
        sb.Append("record_type: glaze_tile\n");
        if (input.Umf is { Count: > 0 } umf)
        {
            sb.Append("umf_profile: ").Append(input.CalculationProfile ?? "unknown").Append('\n');
            AppendGroup(sb, "fluxes", umf, ChemistrySchema.OxideOrder.Where(ChemistrySchema.FluxOxides.Contains));
            AppendGroup(sb, "stabilizers", umf, StabilizerOxides);
            if (TryGet(umf, "B2O3", out var b2o3)) sb.Append("b2o3: ").Append(F(b2o3)).Append('\n');
            AppendGroup(sb, "glass_formers", umf, GlassFormerOxides);
            AppendGroup(sb, "opacifiers", umf, OpacifierOxides);
            AppendGroup(sb, "colourants", umf, ColourantOxides);
            AppendGroup(sb, "other_oxides", umf, OtherOxides);
            if (TryGet(umf, "SiO2", out var sio2) && TryGet(umf, "Al2O3", out var al2o3) && al2o3 > 0)
                sb.Append("silica_alumina_ratio: ").Append(F(sio2 / al2o3)).Append('\n');
        }
        else sb.Append("chemistry: unavailable\n");

        if (input.ConeMin is not null || input.ConeMax is not null)
        {
            var min = input.ConeMin ?? input.ConeMax!.Value;
            var max = input.ConeMax ?? input.ConeMin!.Value;
            sb.Append("cone: ").Append(min == max ? Cone(min) : Cone(min) + "-" + Cone(max)).Append('\n');
        }
        if (!string.IsNullOrWhiteSpace(input.Atmosphere)) sb.Append("atmosphere: ").Append(input.Atmosphere.Trim().ToLowerInvariant()).Append('\n');
        // Images always carry a variant. A query that spans several variants (or none) omits the line rather than guessing.
        if (!string.IsNullOrWhiteSpace(input.TileVariantKey)) sb.Append("tile_variant: ").Append(input.TileVariantKey.Trim().ToLowerInvariant()).Append('\n');
        foreach (var (key, line) in LabelLines)
        {
            if (input.Labels.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
                sb.Append(line).Append(": ").Append(value.Trim().ToLowerInvariant()).Append('\n');
        }
        return sb.ToString();
    }

    public static string Hash(string canonicalText) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Version + "|" + canonicalText))).ToLowerInvariant();

    private static void AppendGroup(StringBuilder sb, string label, IReadOnlyDictionary<string, decimal> umf, IEnumerable<string> oxides)
    {
        var parts = new List<string>();
        foreach (var oxide in oxides) if (TryGet(umf, oxide, out var value)) parts.Add(oxide + "=" + F(value));
        if (parts.Count > 0) sb.Append(label).Append(": ").Append(string.Join("; ", parts)).Append('\n');
    }

    private static bool TryGet(IReadOnlyDictionary<string, decimal> umf, string oxide, out decimal value)
    {
        if (umf.TryGetValue(oxide, out value)) return true;
        foreach (var (key, v) in umf)
            if (ChemistrySchema.TryCanonicalOxide(key, out var canonical) && canonical == oxide) { value = v; return true; }
        value = 0;
        return false;
    }

    private static string F(decimal value) => value.ToString("0.0000", CultureInfo.InvariantCulture);
    private static string Cone(decimal value) => value.ToString("0.##", CultureInfo.InvariantCulture);
}
