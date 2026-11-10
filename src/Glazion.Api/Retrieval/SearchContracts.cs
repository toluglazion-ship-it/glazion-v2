using System.Text.Json;

namespace Glazion.Api.Retrieval;

// Request (plan §10.1–10.3). All fields optional at the type level so validation returns field-level problems, not 415s.
public sealed record RecipeImageSearchRequest(SearchQueryInput? Query, SearchFiringInput? Firing, SearchFiltersInput? Filters, int? TopK, bool? IncludeRecipeMetadata);

public sealed record SearchQueryInput(
    string? Type,
    JsonElement? Umf,
    JsonElement? OxideWeightPercent,
    string? CalculationProfile,
    string? CalculationVersion,
    Guid? RecipeId,
    string? RecipeCode,
    string? Name,
    IReadOnlyList<RawMaterialInput>? Materials);

/// <summary>A recipe row. <c>materialId</c> (from the materials dropdown) wins over <c>rawName</c> when both are given.</summary>
public sealed record RawMaterialInput(string? RawName, decimal? Amount, string? Unit, string? MaterialId = null);

/// <summary>
/// Strict = hard filter: only tiles fired within ±1 cone and in the atmosphere asked for (plan §11.6). Searches are
/// strict whenever the admin setting is on (the default, SearchSettings); then Strict false changes nothing.
/// Otherwise firing only affects the score.
/// </summary>
public sealed record SearchFiringInput(decimal? ConeMin, decimal? ConeMax, string? Atmosphere, bool? Strict);

/// <summary>
/// TileVariantKeys and ClayBodies are hard filters (a tile whose clay body was not recorded never matches a clay body
/// filter). Surface, opacity and colour family are soft preferences.
/// </summary>
public sealed record SearchFiltersInput(IReadOnlyList<string>? TileVariantKeys, IReadOnlyList<string>? Surface, IReadOnlyList<string>? Opacity, IReadOnlyList<string>? ColourFamily,
    IReadOnlyList<string>? ClayBodies = null);

public sealed record SearchFeedbackRequest(Guid SearchId, Guid ImageId, string? Feedback, string? Note);

// Response (plan §10.4), with additive diagnostic fields.
/// <remarks>
/// <c>closestMatch</c> is the best image (items[0]); <c>similar</c> suggests other recipes, the best image of each,
/// excluding the closest match's recipe. Both are additive: <c>items</c> is the ranked list as before.
/// </remarks>
public sealed record RecipeImageSearchResponse(
    Guid SearchId, SearchQuerySummary Query, SearchRankerSummary Ranker,
    IReadOnlyList<SearchResultItem> Items, IReadOnlyList<string> Warnings, DateTimeOffset CreatedAt,
    SearchResultItem? ClosestMatch = null, IReadOnlyList<SearchResultItem>? Similar = null);

/// <summary>
/// <c>umf</c> is the chemistry the search used (calculated from the materials for raw_recipe); <c>colourants</c> the
/// stains it compared (null when unknown, e.g. a UMF query); <c>colorantMaterials</c> every colorant in the recipe, metallic
/// oxides and stains, with its amount, for showing (null when the materials are unknown).
/// </summary>
public sealed record SearchQuerySummary(string Type, string InputHash, string? CalculationProfile, string? RecipeCode,
    IReadOnlyDictionary<string, decimal>? Umf = null, IReadOnlyList<Glazion.Api.Chemistry.Colourant>? Colourants = null,
    IReadOnlyList<Glazion.Api.Chemistry.ColorantMaterial>? ColorantMaterials = null);
public sealed record SearchRankerSummary(string Version, int CandidateCount, int ReturnedCount, double MinimumConfidence);

public sealed record SearchResultItem(
    int Rank,
    string ResultType,
    Guid ImageId,
    Uri? DisplayUrl,
    Uri? ThumbnailUrl,
    double Score,
    string ConfidenceBand,
    SearchScoreBreakdown ScoreBreakdown,
    IReadOnlyList<string> MatchedFields,
    SearchTileSummary Tile,
    SearchFiringSummary Firing,
    SearchSourceSummary Source,
    IReadOnlyList<string> Limitations);

/// <summary>The four ranked dimensions plus the components behind them, so rank changes are diagnosable (plan §11.5).</summary>
public sealed record SearchScoreBreakdown(
    double? ChemistrySimilarity, double? FiringCompatibility, double? LabelOrTextSimilarity, double? ImageSimilarity,
    double? ChemistryCosine, double? ChemistryDistance, double? ChemistryCoverage, double? TextSimilarity, double? LabelMatch,
    double? ColourantSimilarity = null);

public sealed record SearchTileSummary(string Variant, string? Surface, string? Opacity, string? ColourFamily, string? ClayBody = null);
public sealed record SearchFiringSummary(decimal? ConeMin, decimal? ConeMax, string? Atmosphere);
/// <summary>MetadataVisibility is "image_only" unless the requester may see the linked recipe and asked for it.
/// RecipeName is the recipe's name, so a result can be told apart without a lab code; null with "image_only" or a blank name.
/// BaseCode opens the recipe section on the base and all its variations (GET /api/v2/base-glazes/{baseCode}); null for a
/// recipe outside the base register.</summary>
public sealed record SearchSourceSummary(string RecordType, Guid? RecipeId, string? RecipeCode, string MetadataVisibility, string? RecipeName = null, string? BaseCode = null);

public sealed record SearchFeedbackResponse(Guid Id, Guid SearchId, Guid ImageId, string Feedback, string? Note, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record SearchRequester(Guid UserId, bool IsAdmin);
