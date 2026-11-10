using System.Text.RegularExpressions;

namespace Glazion.Api.Infrastructure;

/// <summary>
/// The only place object keys are built. Keys are derived from API-generated identifiers and the declared
/// content type, never from file names, user names, capture codes, or any other client-controlled string.
/// Layout follows PRD §9 / plan §6.2 and is stable so later environments and tenants never require migration.
/// </summary>
public static partial class ObjectKeyPolicy
{
    public const string RootPrefix = "prod";
    public const string DefaultTenant = "default";
    public const string SchemaVersion = "image-asset-v1";

    [GeneratedRegex("^[A-Za-z0-9._-]{1,128}$")]
    private static partial Regex ExternalRecordIdPattern();

    public static bool IsValidExternalRecordId(string? value) => !string.IsNullOrEmpty(value) && ExternalRecordIdPattern().IsMatch(value);

    /// <summary>Extension is derived from the declared content type, never from the file name.</summary>
    public static string ExtensionFor(string contentType) => contentType.ToLowerInvariant() switch
    {
        "image/jpeg" => "jpg",
        "image/png" => "png",
        "image/webp" => "webp",
        _ => throw new ArgumentException($"Unsupported content type '{contentType}'.", nameof(contentType))
    };

    public static string UploadOriginal(Guid uploadId, string contentType, string tenantId = DefaultTenant)
        => $"{RootPrefix}/originals/{tenantId}/uploads/{uploadId:D}/source.{ExtensionFor(contentType)}";

    /// <summary>The back of an uploaded tile (its code), next to the front.</summary>
    public static string UploadBack(Guid uploadId, string contentType, string tenantId = DefaultTenant)
        => $"{RootPrefix}/originals/{tenantId}/uploads/{uploadId:D}/back.{ExtensionFor(contentType)}";

    public static string ImportOriginal(Guid batchId, string externalRecordId, string contentType, string tenantId = DefaultTenant)
    {
        if (!IsValidExternalRecordId(externalRecordId)) throw new ArgumentException("externalRecordId must match [A-Za-z0-9._-]{1,128}.", nameof(externalRecordId));
        return $"{RootPrefix}/originals/{tenantId}/imports/{batchId:D}/{externalRecordId}/source.{ExtensionFor(contentType)}";
    }

    public static string Display(Guid imageId, string tenantId = DefaultTenant) => $"{RootPrefix}/derivatives/{tenantId}/images/{imageId:D}/display.webp";
    public static string Thumbnail(Guid imageId, string tenantId = DefaultTenant) => $"{RootPrefix}/derivatives/{tenantId}/images/{imageId:D}/thumbnail.webp";

    /// <summary>An AI copy of the image (docs/features/ai-copies.md), at display size and as a thumbnail. Kept beside the image's own derivatives.</summary>
    public static string AiCopy(Guid imageId, Guid copyId, string tenantId = DefaultTenant) => $"{RootPrefix}/derivatives/{tenantId}/images/{imageId:D}/ai/{copyId:D}.webp";
    public static string AiCopyThumbnail(Guid imageId, Guid copyId, string tenantId = DefaultTenant) => $"{RootPrefix}/derivatives/{tenantId}/images/{imageId:D}/ai/{copyId:D}-thumbnail.webp";

    /// <summary>Quarantine mirrors the original path so a quarantined object maps back to its row without a lookup table.</summary>
    public static string Quarantine(string originalKey)
    {
        const string marker = "/originals/";
        var index = originalKey.IndexOf(marker, StringComparison.Ordinal);
        if (index < 0) throw new ArgumentException("Only originals can be quarantined.", nameof(originalKey));
        return string.Concat(originalKey.AsSpan(0, index), "/quarantine/", originalKey.AsSpan(index + marker.Length));
    }
}
