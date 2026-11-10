using System.Globalization;
using Glazion.Api.Contracts;
using Glazion.Api.Domain;

namespace Glazion.Api.Infrastructure;

/// <summary>
/// Filters for GET /admin/images. Every filter is optional and they combine with AND.
/// recipeCode is a full lab code (A03-B2-009) or a prefix of one (A03-B2, A03); clayBody "none" finds images without
/// one; cone takes "06" or -6 for cone 06 and matches images whose recorded cone range includes it; q looks in file
/// names, external record ids, recipe names and lab codes.
/// </summary>
public sealed record AdminImageQuery(
    ApprovalStatus? Approval = null, QualityStatus? Quality = null, ChemistryStatus? Chemistry = null,
    string? RecipeCode = null, string? ClayBody = null, Guid? ImportBatchId = null, string? ParticipantCode = null,
    string? Cone = null, bool? HasThumbnail = null, string? Q = null);

public static class AdminImageFilter
{
    public const string NoClayBody = "none";

    public static IQueryable<ImageAsset> Apply(IQueryable<ImageAsset> query, AdminImageQuery filter, IngestionOptions ingestion)
    {
        if (filter.Approval is { } approval) query = query.Where(x => x.ApprovalStatus == approval);
        if (filter.Quality is { } quality) query = query.Where(x => x.QualityStatus == quality);
        if (filter.Chemistry is { } chemistry) query = query.Where(x => x.ChemistryStatus == chemistry);

        if (!string.IsNullOrWhiteSpace(filter.RecipeCode))
        {
            var code = filter.RecipeCode.Trim().ToUpperInvariant();
            if (LabCode.NormalizeRecipe(code) is { } full) query = query.Where(x => x.Recipes.Any(r => r.Recipe.Code == full));
            else
            {
                var prefix = code.TrimEnd('-') + "-";
                query = query.Where(x => x.Recipes.Any(r => r.Recipe.Code != null && r.Recipe.Code.StartsWith(prefix)));
            }
        }

        if (!string.IsNullOrWhiteSpace(filter.ClayBody))
        {
            if (string.Equals(filter.ClayBody.Trim(), NoClayBody, StringComparison.OrdinalIgnoreCase)) query = query.Where(x => x.ClayBody == null);
            else if (ingestion.TryNormalizeClayBody(filter.ClayBody, out var clayBody)) query = query.Where(x => x.ClayBody == clayBody);
            else throw ApiProblems.Validation("clayBody", $"clayBody must be one of {string.Join(", ", ingestion.AllowedClayBodies)}, or '{NoClayBody}'.");
        }

        if (filter.ImportBatchId is { } batchId) query = query.Where(x => x.ImportRecord != null && x.ImportRecord.BatchId == batchId);
        if (!string.IsNullOrWhiteSpace(filter.ParticipantCode)) query = ClayBodyBackfill.ForParticipant(query, filter.ParticipantCode);

        if (!string.IsNullOrWhiteSpace(filter.Cone))
        {
            var cone = ParseCone(filter.Cone) ?? throw ApiProblems.Validation("cone", "cone must be a cone number such as 6, 10 or 06 (-6 also means cone 06).");
            query = query.Where(x => (x.ConeMin != null || x.ConeMax != null) && (x.ConeMin ?? x.ConeMax) <= cone && (x.ConeMax ?? x.ConeMin) >= cone);
        }

        if (filter.HasThumbnail is { } hasThumbnail) query = hasThumbnail ? query.Where(x => x.ThumbnailKey != null) : query.Where(x => x.ThumbnailKey == null);

        if (!string.IsNullOrWhiteSpace(filter.Q))
        {
            var term = filter.Q.Trim().ToLowerInvariant();
            query = query.Where(x =>
                (x.ImportRecord != null && (x.ImportRecord.FileName.ToLower().Contains(term) || x.ImportRecord.ExternalRecordId.ToLower().Contains(term)))
                || (x.Upload != null && x.Upload.OriginalFileName != null && x.Upload.OriginalFileName.ToLower().Contains(term))
                || x.Recipes.Any(r => r.Recipe.Name.ToLower().Contains(term) || (r.Recipe.Code != null && r.Recipe.Code.ToLower().Contains(term))));
        }
        return query;
    }

    /// <summary>Cones as the apps write them: a leading zero means a 0x cone ("06" is -6). Null when it isn't a cone.</summary>
    public static decimal? ParseCone(string value)
    {
        var text = value.Trim();
        if (!decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var cone)) return null;
        if (text.Length > 1 && text[0] == '0' && text[1] != '.' && cone > 0) cone = -cone;
        return cone is < -20 or > 20 ? null : cone;
    }
}
