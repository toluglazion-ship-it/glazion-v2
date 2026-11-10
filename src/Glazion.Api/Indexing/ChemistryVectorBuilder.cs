using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Glazion.Api.Infrastructure;

namespace Glazion.Api.Indexing;

/// <summary>
/// A fixed-order chemistry vector under a versioned oxide schema (PRD §11.2, plan §11.1). Values are UMF molar
/// amounts in ChemistrySchema.OxideOrder. Unmeasured oxides are zero in Values but false in Measured, so completeness
/// stays distinguishable from a measured zero when confidence is calculated.
/// </summary>
public sealed record ChemistryVector(string SchemaVersion, float[] Values, bool[] Measured, string Hash)
{
    public int Dimension => Values.Length;
    public int MeasuredCount => Measured.Count(x => x);
    public double Completeness => Measured.Length == 0 ? 0 : (double)MeasuredCount / Measured.Length;

    /// <summary>Unit-length copy for cosine distance providers. A zero vector stays zero rather than producing NaN.</summary>
    public float[] Normalized()
    {
        var norm = Math.Sqrt(Values.Sum(v => (double)v * v));
        if (norm == 0) return new float[Values.Length];
        return Values.Select(v => (float)(v / norm)).ToArray();
    }
}

public static class ChemistryVectorBuilder
{
    public static ChemistryVector Build(IReadOnlyDictionary<string, decimal> umf, string schemaVersion = ChemistrySchema.Version)
    {
        if (schemaVersion != ChemistrySchema.Version) throw new ArgumentException($"Unknown chemistry schema '{schemaVersion}'.", nameof(schemaVersion));
        var order = ChemistrySchema.OxideOrder;
        var values = new float[order.Count];
        var measured = new bool[order.Count];
        var canonical = new SortedDictionary<string, decimal>(StringComparer.Ordinal);
        for (var i = 0; i < order.Count; i++)
        {
            if (!TryGet(umf, order[i], out var value)) continue;
            values[i] = (float)value;
            measured[i] = true;
            canonical[order[i]] = value;
        }
        // Hash covers schema version + canonical values so a schema change always yields a different hash.
        var payload = schemaVersion + "|" + ChemistrySchema.CanonicalJson(canonical);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
        return new ChemistryVector(schemaVersion, values, measured, hash);
    }

    public static ChemistryVector FromUmfJson(string umfJson, string schemaVersion = ChemistrySchema.Version)
    {
        using var document = JsonDocument.Parse(umfJson);
        var validation = ChemistrySchema.ValidateUmf(document.RootElement);
        if (!validation.IsValid) throw new InvalidOperationException("UMF is not valid for vector generation: " + string.Join(",", validation.Errors));
        return Build(validation.Umf, schemaVersion);
    }

    public static double CosineSimilarity(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length != b.Length) throw new ArgumentException("Vector dimensions differ.");
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++) { dot += (double)a[i] * b[i]; na += (double)a[i] * a[i]; nb += (double)b[i] * b[i]; }
        return na == 0 || nb == 0 ? 0 : dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }

    private static bool TryGet(IReadOnlyDictionary<string, decimal> umf, string oxide, out decimal value)
    {
        if (umf.TryGetValue(oxide, out value)) return true;
        foreach (var (key, v) in umf)
        {
            if (ChemistrySchema.TryCanonicalOxide(key, out var canonical) && canonical == oxide) { value = v; return true; }
        }
        value = 0;
        return false;
    }
}
