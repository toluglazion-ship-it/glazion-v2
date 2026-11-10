using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Glazion.Api.Infrastructure;

public sealed record UmfValidationResult(bool IsValid, IReadOnlyDictionary<string, decimal> Umf, decimal FluxTotal, IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings)
{
    public string CanonicalJson => ChemistrySchema.CanonicalJson(Umf);
    public string InputHash => ChemistrySchema.Hash(CanonicalJson);
}

/// <summary>
/// The versioned fixed oxide order shared by index records and queries (PRD §11.2, plan §11.1). Validation here
/// is intentionally strict: the retrieval engine must never receive an oxide it cannot place in the vector.
/// Absent oxides stay absent; they are not written as zero so completeness remains distinguishable.
/// </summary>
public static class ChemistrySchema
{
    public const string Version = "oxide-vector-v1";
    public const string ProvidedUmfProfile = "provided-umf-v1";
    public const string ProvidedUmfCalculationVersion = "import-1.0.0";
    public const string NoMaterialAnalysis = "n/a";

    public static readonly IReadOnlyList<string> OxideOrder =
    [
        "Li2O", "Na2O", "K2O", "MgO", "CaO", "SrO", "BaO", "ZnO", "PbO",
        "B2O3", "Al2O3", "SiO2", "TiO2", "ZrO2", "SnO2", "Fe2O3",
        "Cr2O3", "CuO", "CoO", "MnO", "NiO", "P2O5"
    ];

    /// <summary>Traditional flux set normalised to unity in the default UMF profile (PRD UMF-05).</summary>
    public static readonly IReadOnlySet<string> FluxOxides = new HashSet<string>(StringComparer.Ordinal) { "Li2O", "Na2O", "K2O", "MgO", "CaO", "SrO", "BaO", "ZnO", "PbO" };

    private static readonly Dictionary<string, string> Canonical = OxideOrder.ToDictionary(x => x.ToLowerInvariant(), x => x, StringComparer.Ordinal);
    private const decimal UnityTolerance = 0.02m;

    public static bool TryCanonicalOxide(string name, out string canonical)
    {
        canonical = string.Empty;
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (!Canonical.TryGetValue(name.Trim().ToLowerInvariant(), out var found)) return false;
        canonical = found;
        return true;
    }

    public static UmfValidationResult ValidateUmf(JsonElement element)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        var umf = new SortedDictionary<string, decimal>(Comparer<string>.Create((a, b) => OxideOrder.IndexOf(a).CompareTo(OxideOrder.IndexOf(b))));

        if (element.ValueKind != JsonValueKind.Object)
        {
            errors.Add("umf_not_object");
            return new UmfValidationResult(false, umf, 0, errors, warnings);
        }

        decimal ferrous = 0;
        foreach (var property in element.EnumerateObject())
        {
            // FeO has no slot of its own. Its iron is counted as Fe2O3 (two FeO carry the iron of one Fe2O3), so black
            // iron oxide, ilmenite and the like still count as iron rather than being left out.
            if (property.Name.Trim() == "FeO" && property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetDecimal(out var feo) && feo >= 0)
            {
                ferrous += feo;
                continue;
            }
            if (!TryCanonicalOxide(property.Name, out var oxide))
            {
                // An oxide the calculator knows (V2O5, CeO2, MoO3, …) but this vector schema has no slot for is
                // dropped with an explicit warning; a symbol nobody knows is still an error.
                if (Glazion.Api.Chemistry.GlazeChemistry.KnownOxideSymbols.Contains(property.Name.Trim())) warnings.Add($"oxide_outside_vector_schema:{property.Name.Trim()}");
                else errors.Add($"unknown_oxide:{property.Name}");
                continue;
            }
            if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetDecimal(out var value) || value < 0) { errors.Add($"invalid_oxide_value:{oxide}"); continue; }
            if (umf.ContainsKey(oxide)) { errors.Add($"duplicate_oxide:{oxide}"); continue; }
            umf[oxide] = value;
        }

        if (ferrous > 0) umf["Fe2O3"] = umf.GetValueOrDefault("Fe2O3") + ferrous / 2;
        if (umf.Count == 0 && errors.Count == 0) errors.Add("umf_empty");
        var fluxTotal = umf.Where(x => FluxOxides.Contains(x.Key)).Sum(x => x.Value);
        if (errors.Count == 0)
        {
            if (fluxTotal == 0) errors.Add("zero_flux");
            else if (Math.Abs(fluxTotal - 1m) > UnityTolerance) warnings.Add("flux_total_not_unity");
        }
        return new UmfValidationResult(errors.Count == 0, umf, fluxTotal, errors, warnings);
    }

    public static string CanonicalJson(IReadOnlyDictionary<string, decimal> umf)
    {
        var sb = new StringBuilder("{");
        var first = true;
        foreach (var oxide in OxideOrder)
        {
            if (!umf.TryGetValue(oxide, out var value)) continue;
            if (!first) sb.Append(',');
            first = false;
            sb.Append('"').Append(oxide).Append("\":").Append(value.ToString("0.##########", CultureInfo.InvariantCulture));
        }
        return sb.Append('}').ToString();
    }

    public static string Hash(string canonical) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();

    private static int IndexOf(this IReadOnlyList<string> list, string value)
    {
        for (var i = 0; i < list.Count; i++) if (list[i] == value) return i;
        return int.MaxValue;
    }
}
