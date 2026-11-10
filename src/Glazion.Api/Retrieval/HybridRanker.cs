using Glazion.Api.Indexing;
using Glazion.Api.Infrastructure;

namespace Glazion.Api.Retrieval;

public sealed class FeatureOptions
{
    /// <summary>
    /// R2I switch (plan Phase 0/4). System scoped: on or off for every signed-in account alike, admins included.
    /// On by default; set Features__RecipeImageRetrieval=false to pause search for everyone.
    /// </summary>
    public bool RecipeImageRetrieval { get; set; } = true;
}

public sealed class RetrievalOptions
{
    /// <summary>1.1.0 adds the colourant dimension (stains the UMF cannot see); 1.2.0 makes it both kinds of colorant (ColorantMatch).</summary>
    public string RankerVersion { get; set; } = "hybrid-ranker-1.2.0";
    public RankerWeights Weights { get; set; } = new();
    /// <summary>Results scoring below this are not returned; an empty result carries no_reliable_match (R2I-07).</summary>
    public double MinimumConfidence { get; set; } = 0.45;
    public double HighConfidence { get; set; } = 0.80;
    public double MediumConfidence { get; set; } = 0.60;
    /// <summary>Plan §11.4: candidate topK = max(MinCandidates, requestedTopK × CandidateMultiplier), capped.</summary>
    public int MinCandidates { get; set; } = 50;
    public int CandidateMultiplier { get; set; } = 8;
    public int MaxCandidates { get; set; } = 400;
    public int MaxTopK { get; set; } = 50;
    public int DefaultTopK { get; set; } = 12;
    /// <summary>Cone ranges this many cones apart still count as "within configured range".</summary>
    public decimal ConeTolerance { get; set; } = 1m;
    /// <summary>Share of chemistry similarity taken from cosine; the rest comes from the bounded weighted distance.</summary>
    public double ChemistryCosineShare { get; set; } = 0.5;
    /// <summary>A candidate measuring fewer than this share of the query's oxides drops one confidence band.</summary>
    public double MinimumChemistryCoverage { get; set; } = 0.75;
    /// <summary>How many other recipes to suggest next to the closest match (one image each).</summary>
    public int SimilarCount { get; set; } = 8;
}

public sealed class RankerWeights
{
    public double Chemistry { get; set; } = 0.55;
    public double Firing { get; set; } = 0.20;
    public double LabelOrText { get; set; } = 0.15;
    public double Image { get; set; } = 0.10;
    /// <summary>
    /// Colorants, both kinds (ColorantMatch): metallic oxides from the UMF, and stains by material and amount when both
    /// sides know them. The colour is what a person sees first, so it weighs close to the chemistry of the whole glaze.
    /// Dropped only when the tile has no chemistry.
    /// </summary>
    public double Colourant { get; set; } = 0.45;
}

public enum ConeMatch { NotRequested, Exact, Compatible, Unknown, Conflict }
public enum AtmosphereMatch { NotRequested, Exact, Unknown, Conflict }

public sealed record FiringQuery(decimal? ConeMin, decimal? ConeMax, string? Atmosphere)
{
    public bool HasCone => ConeMin is not null || ConeMax is not null;
    public bool HasAtmosphere => !string.IsNullOrWhiteSpace(Atmosphere);
    public bool IsEmpty => !HasCone && !HasAtmosphere;
}

public sealed record FiringResult(double? Score, ConeMatch Cone, AtmosphereMatch Atmosphere);

public sealed record ChemistryComparison(
    double Similarity, double Cosine, double Distance, double DistanceSimilarity,
    double Coverage, IReadOnlyList<string> MissingQueryOxides,
    double? FluxCosine, decimal? SilicaDelta, decimal? AluminaDelta, decimal? BoronDelta, double? SilicaAluminaRatioDelta);

/// <summary>
/// The transparent hybrid ranker (PRD §5.6, plan §11.5), version hybrid-ranker-1.0.0. Every function is pure and
/// deterministic so a score can be recomputed from the search run snapshot. The oxide scale and importance tables
/// below are part of ranker version 1.0.0; changing them requires a new RankerVersion.
/// </summary>
public static class HybridRanker
{
    /// <summary>Typical meaningful UMF difference per oxide. A difference of one scale unit everywhere gives distance 1.</summary>
    private static readonly Dictionary<string, double> OxideScale = new(StringComparer.Ordinal)
    {
        ["Li2O"] = 0.10, ["Na2O"] = 0.10, ["K2O"] = 0.10, ["MgO"] = 0.10, ["CaO"] = 0.10, ["SrO"] = 0.10, ["BaO"] = 0.10, ["ZnO"] = 0.10, ["PbO"] = 0.10,
        ["B2O3"] = 0.10, ["Al2O3"] = 0.10, ["SiO2"] = 0.50,
        ["TiO2"] = 0.05, ["ZrO2"] = 0.05, ["SnO2"] = 0.05,
        ["Fe2O3"] = 0.02, ["Cr2O3"] = 0.02, ["CuO"] = 0.02, ["CoO"] = 0.02, ["MnO"] = 0.02, ["NiO"] = 0.02, ["P2O5"] = 0.02
    };

    /// <summary>Controlled importance: the glass-forming skeleton (silica, alumina, boron) counts more than any single flux or colourant.</summary>
    private static readonly Dictionary<string, double> OxideImportance = new(StringComparer.Ordinal)
    {
        ["SiO2"] = 1.5, ["Al2O3"] = 1.5, ["B2O3"] = 1.2, ["P2O5"] = 0.5
    };

    public static ChemistryComparison CompareChemistry(IReadOnlyDictionary<string, decimal> query, IReadOnlyDictionary<string, decimal> candidate, double cosineShare)
    {
        var order = ChemistrySchema.OxideOrder;
        var q = ChemistryVectorBuilder.Build(query);
        var c = ChemistryVectorBuilder.Build(candidate);
        var cosine = Math.Max(0, ChemistryVectorBuilder.CosineSimilarity(q.Values, c.Values));

        double weighted = 0, weightTotal = 0;
        var missing = new List<string>();
        int queryMeasured = 0, covered = 0;
        for (var i = 0; i < order.Count; i++)
        {
            var oxide = order[i];
            if (q.Measured[i]) { queryMeasured++; if (c.Measured[i]) covered++; else missing.Add(oxide); }
            if (!q.Measured[i] && !c.Measured[i]) continue; // neither side reports it: no evidence either way
            var w = OxideImportance.GetValueOrDefault(oxide, 1.0);
            var diff = (q.Values[i] - c.Values[i]) / OxideScale[oxide];
            weighted += w * diff * diff;
            weightTotal += w;
        }
        var distance = weightTotal == 0 ? 0 : Math.Sqrt(weighted / weightTotal);
        var distanceSimilarity = 1.0 / (1.0 + distance);
        var share = Math.Clamp(cosineShare, 0, 1);
        var similarity = share * cosine + (1 - share) * distanceSimilarity;

        var fluxIdx = Enumerable.Range(0, order.Count).Where(i => ChemistrySchema.FluxOxides.Contains(order[i])).ToArray();
        var qf = fluxIdx.Select(i => q.Values[i]).ToArray();
        var cf = fluxIdx.Select(i => c.Values[i]).ToArray();
        double? fluxCosine = qf.Any(v => v != 0) && cf.Any(v => v != 0) ? ChemistryVectorBuilder.CosineSimilarity(qf, cf) : null;

        decimal? Delta(string oxide) => query.TryGetValue(oxide, out var a) && candidate.TryGetValue(oxide, out var b) ? Math.Abs(a - b) : null;
        double? ratioDelta = null;
        if (query.TryGetValue("SiO2", out var qs) && query.TryGetValue("Al2O3", out var qa) && qa > 0 && candidate.TryGetValue("SiO2", out var cs) && candidate.TryGetValue("Al2O3", out var ca) && ca > 0)
        {
            var qr = (double)(qs / qa); var cr = (double)(cs / ca);
            ratioDelta = Math.Abs(qr - cr) / qr;
        }
        return new ChemistryComparison(similarity, cosine, distance, distanceSimilarity,
            queryMeasured == 0 ? 0 : (double)covered / queryMeasured, missing, fluxCosine,
            Delta("SiO2"), Delta("Al2O3"), Delta("B2O3"), ratioDelta);
    }

    /// <summary>
    /// Deterministic firing compatibility (plan §11.5). Table values from the plan:
    /// 1.00 exact cone + exact atmosphere; 0.85 cone within range + exact atmosphere; 0.60 compatible cone + unknown
    /// atmosphere; 0.30 conflicting atmosphere or out-of-range cone; 0.00 is reserved for hard-filter incompatibility,
    /// which never reaches the ranker because hard filters remove those candidates.
    /// Extensions for cases the table does not list are documented in docs/architecture/recipe-to-image/04-search.md §4.2.
    /// </summary>
    /// <summary>Cones apart, from a lower cone to a higher one. There is no cone 0, so 01 (-1) and 1 are one apart.</summary>
    public static decimal ConeSteps(decimal low, decimal high) => low < 0 && high > 0 ? high - low - 1 : high - low;

    /// <summary>The cones within <paramref name="tolerance"/> of a searched range, stepping over the missing cone 0.</summary>
    public static (decimal Min, decimal Max) ConeWindow(decimal min, decimal max, decimal tolerance)
    {
        var low = min - tolerance;
        if (min > 0 && low <= 0) low -= 1;
        var high = max + tolerance;
        if (max < 0 && high >= 0) high += 1;
        return (low, high);
    }

    public static FiringResult Firing(FiringQuery query, decimal? coneMin, decimal? coneMax, string? atmosphere, decimal coneTolerance)
    {
        if (query.IsEmpty) return new FiringResult(null, ConeMatch.NotRequested, AtmosphereMatch.NotRequested);

        var cone = ConeMatch.NotRequested;
        if (query.HasCone)
        {
            var qMin = query.ConeMin ?? query.ConeMax!.Value;
            var qMax = query.ConeMax ?? query.ConeMin!.Value;
            if (coneMin is null && coneMax is null) cone = ConeMatch.Unknown;
            else
            {
                var cMin = coneMin ?? coneMax!.Value;
                var cMax = coneMax ?? coneMin!.Value;
                if (cMin == qMin && cMax == qMax) cone = ConeMatch.Exact;
                else
                {
                    var gap = cMax < qMin ? ConeSteps(cMax, qMin) : cMin > qMax ? ConeSteps(qMax, cMin) : 0m; // 0 when ranges overlap
                    cone = gap <= coneTolerance ? ConeMatch.Compatible : ConeMatch.Conflict;
                }
            }
        }

        var atm = AtmosphereMatch.NotRequested;
        if (query.HasAtmosphere)
        {
            if (string.IsNullOrWhiteSpace(atmosphere)) atm = AtmosphereMatch.Unknown;
            else atm = string.Equals(atmosphere.Trim(), query.Atmosphere!.Trim(), StringComparison.OrdinalIgnoreCase) ? AtmosphereMatch.Exact : AtmosphereMatch.Conflict;
        }

        double score;
        if (cone == ConeMatch.Conflict || atm == AtmosphereMatch.Conflict) score = 0.30;
        else if (atm == AtmosphereMatch.NotRequested) score = cone switch { ConeMatch.Exact => 1.00, ConeMatch.Compatible => 0.85, _ => 0.50 };
        else if (cone == ConeMatch.NotRequested) score = atm == AtmosphereMatch.Exact ? 1.00 : 0.60;
        else score = (cone, atm) switch
        {
            (ConeMatch.Exact, AtmosphereMatch.Exact) => 1.00,
            (ConeMatch.Compatible, AtmosphereMatch.Exact) => 0.85,
            (ConeMatch.Exact or ConeMatch.Compatible, AtmosphereMatch.Unknown) => 0.60,
            (ConeMatch.Unknown, AtmosphereMatch.Exact) => 0.60,
            _ => 0.50 // cone and atmosphere both unrecorded
        };
        return new FiringResult(score, cone, atm);
    }

    /// <summary>Weighted mean over the dimensions that are available; unavailable ones are dropped and the rest renormalised.</summary>
    public static double Combine(RankerWeights weights, double? chemistry, double? firing, double? labelOrText, double? image, double? colourant = null)
    {
        double sum = 0, total = 0;
        void Add(double w, double? v) { if (v is { } x) { sum += w * Math.Clamp(x, 0, 1); total += w; } }
        Add(weights.Chemistry, chemistry);
        Add(weights.Firing, firing);
        Add(weights.LabelOrText, labelOrText);
        Add(weights.Image, image);
        Add(weights.Colourant, colourant);
        return total == 0 ? 0 : sum / total;
    }

    public static string Band(double score, int reductions, RetrievalOptions options)
    {
        var level = score >= options.HighConfidence ? 2 : score >= options.MediumConfidence ? 1 : 0;
        return Math.Max(0, level - reductions) switch { 2 => "high", 1 => "medium", _ => "low" };
    }
}
