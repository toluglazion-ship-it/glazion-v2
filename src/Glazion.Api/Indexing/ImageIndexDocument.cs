using System.Text.Json;
using Glazion.Api.Domain;
using Glazion.Api.Infrastructure;

namespace Glazion.Api.Indexing;

/// <summary>Metadata keys stored next to vectors. Allow-listed and non-sensitive (plan §14): filterable facts only.</summary>
public static class VectorMetadataKeys
{
    public const string ImageId = "imageId";
    public const string SourceVersion = "sourceVersion";
    public const string ApprovalStatus = "approvalStatus";
    public const string Indexable = "indexable";
    public const string Visibility = "visibility";
    public const string TileVariant = "tileVariant";
    public const string ConeMin = "coneMin";
    public const string ConeMax = "coneMax";
    public const string Atmosphere = "atmosphere";
    public const string ClayBody = "clayBody";
    public const string ChemistryStatus = "chemistryStatus";
    public const string ChemistryCompleteness = "chemistryCompleteness";
    public const string Surface = "surface";
    public const string Opacity = "opacity";
    public const string ColourFamily = "colourFamily";
    public const string QualityStatus = "qualityStatus";
    public const string RecipeId = "recipeId";
    public const string RecipeCode = "recipeCode";
    public const string Modality = "modality";
    /// <summary>Material ids of the image's stains (materials with no oxide analysis), so tiles sharing a stain can be pulled in even when hundreds share one UMF.</summary>
    public const string Colourants = "colourants";
    /// <summary>True when the image's recipe adds a stain, false when it adds none; absent when unknown.</summary>
    public const string Stained = "stained";
}

/// <summary>
/// The versioned, provider-neutral document built for one image at one SourceVersion. It carries everything the
/// indexing jobs need (vectors, text, metadata, pinned versions) and nothing they must not see. Replaces the
/// dataset-only DTOs from the prototype (plan §17).
/// </summary>
public sealed record ImageIndexDocument(
    Guid ImageId,
    long SourceVersion,
    bool Indexable,
    ChemistryVector? Chemistry,
    string CanonicalText,
    string CanonicalTextHash,
    IReadOnlyDictionary<string, object> Metadata,
    ImageIndexVersions Versions);

public sealed record ImageIndexVersions(
    string ChemistrySchemaVersion,
    string CanonicalTextVersion,
    string? CalculationProfile,
    string? CalculationVersion,
    string? MaterialAnalysisVersion,
    string? QualityPolicyVersion);

public static class ImageIndexDocumentBuilder
{
    private static readonly JsonSerializerOptions LabelJson = new(JsonSerializerDefaults.Web);

    /// <summary>Requires asset.CalculationResult to be loaded when ChemistryStatus is Linked.</summary>
    public static ImageIndexDocument Build(ImageAsset asset, Guid? primaryRecipeId = null, IReadOnlyList<Chemistry.Colourant>? colourants = null)
    {
        var labels = ParseLabels(asset.LabelsJson);
        IReadOnlyDictionary<string, decimal>? umf = null;
        ChemistryVector? chemistry = null;
        if (asset.ChemistryStatus == ChemistryStatus.Linked)
        {
            var snapshot = asset.CalculationResult ?? throw new InvalidOperationException($"Image {asset.Id} is chemistry-linked but its calculation result is not loaded.");
            using var document = JsonDocument.Parse(snapshot.UmfJson);
            var validation = ChemistrySchema.ValidateUmf(document.RootElement);
            if (validation.IsValid)
            {
                umf = validation.Umf;
                chemistry = ChemistryVectorBuilder.Build(umf);
            }
        }

        var text = CanonicalTextBuilder.Build(new CanonicalTextInput(asset.CalculationResult?.CalculationProfile, umf, asset.ConeMin, asset.ConeMax, asset.Atmosphere, asset.TileVariantKey, labels));
        var indexable = asset.ApprovalStatus == ApprovalStatus.Approved && asset.QualityStatus != QualityStatus.Failed;

        var metadata = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            [VectorMetadataKeys.ImageId] = asset.Id.ToString("D"),
            [VectorMetadataKeys.SourceVersion] = asset.SourceVersion,
            [VectorMetadataKeys.ApprovalStatus] = asset.ApprovalStatus.ToString().ToLowerInvariant(),
            [VectorMetadataKeys.Indexable] = indexable,
            [VectorMetadataKeys.Visibility] = "approved_users",
            [VectorMetadataKeys.TileVariant] = asset.TileVariantKey,
            [VectorMetadataKeys.ChemistryStatus] = asset.ChemistryStatus.ToString().ToLowerInvariant(),
            [VectorMetadataKeys.QualityStatus] = asset.QualityStatus.ToString().ToLowerInvariant()
        };
        if (asset.ConeMin is { } coneMin) metadata[VectorMetadataKeys.ConeMin] = (double)coneMin;
        if (asset.ConeMax is { } coneMax) metadata[VectorMetadataKeys.ConeMax] = (double)coneMax;
        if (!string.IsNullOrWhiteSpace(asset.Atmosphere)) metadata[VectorMetadataKeys.Atmosphere] = asset.Atmosphere.ToLowerInvariant();
        // Metadata only, not canonical text: recording a clay body refreshes the vector without a new embedding.
        if (!string.IsNullOrWhiteSpace(asset.ClayBody)) metadata[VectorMetadataKeys.ClayBody] = asset.ClayBody;
        if (chemistry is not null) metadata[VectorMetadataKeys.ChemistryCompleteness] = Math.Round(chemistry.Completeness, 4);
        if (labels.TryGetValue("surface", out var surface)) metadata[VectorMetadataKeys.Surface] = surface.ToLowerInvariant();
        if (labels.TryGetValue("opacity", out var opacity)) metadata[VectorMetadataKeys.Opacity] = opacity.ToLowerInvariant();
        if (labels.TryGetValue("colourFamily", out var colour)) metadata[VectorMetadataKeys.ColourFamily] = colour.ToLowerInvariant();
        var primary = asset.Recipes.OrderByDescending(x => x.Verified).ThenBy(x => x.CreatedAt).FirstOrDefault(x => primaryRecipeId is null || x.RecipeId == primaryRecipeId);
        var recipeId = primaryRecipeId ?? primary?.RecipeId;
        if (recipeId is { } r) metadata[VectorMetadataKeys.RecipeId] = r.ToString("D");
        if (primary?.Recipe?.Code is { } recipeCode) metadata[VectorMetadataKeys.RecipeCode] = recipeCode;
        if (colourants is { Count: > 0 }) metadata[VectorMetadataKeys.Colourants] = colourants.Select(c => c.MaterialId).ToList();
        if (colourants is not null) metadata[VectorMetadataKeys.Stained] = colourants.Count > 0;

        var versions = new ImageIndexVersions(ChemistrySchema.Version, CanonicalTextBuilder.Version,
            asset.CalculationResult?.CalculationProfile, asset.CalculationResult?.CalculationVersion, asset.CalculationResult?.MaterialAnalysisVersion, asset.QualityPolicyVersion);
        return new ImageIndexDocument(asset.Id, asset.SourceVersion, indexable, chemistry, text, CanonicalTextBuilder.Hash(text), metadata, versions);
    }

    private static Dictionary<string, string> ParseLabels(string labelsJson)
    {
        if (string.IsNullOrWhiteSpace(labelsJson)) return new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            using var document = JsonDocument.Parse(labelsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return new Dictionary<string, string>(StringComparer.Ordinal);
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
                if (property.Value.ValueKind == JsonValueKind.String) result[property.Name] = property.Value.GetString()!;
            return result;
        }
        catch (JsonException) { return new Dictionary<string, string>(StringComparer.Ordinal); }
    }
}
