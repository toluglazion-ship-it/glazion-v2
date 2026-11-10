using Glazion.Api.Domain;
using Glazion.Api.Infrastructure;

namespace Glazion.Api.Contracts;

// ---- v2 core contract ----
// Mirrors @glazion/types (Realdiamond/glazion packages/types/src/index.ts) field for field; enums travel as numbers.
// Fields the clients do not know yet are additive and listed after the mirrored ones.

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int TotalCount, string? NextCursor);

public sealed record LoginCommand(string Username, string Password);
public sealed record AuthTokensResult(string AccessToken, string RefreshToken, int ExpiresInSeconds);
public sealed record LoginResult(AuthTokensResult Tokens, Guid UserId, string Username, string? DisplayName, UserRole Role, AccountStatus AccountStatus);
public sealed record RefreshCommand(string RefreshToken);
public sealed record RegisterRequest(string DisplayName, string Username, string Email, string Password);
public sealed record RegisterResult(Guid UserId, AccountStatus AccountStatus);

public sealed record CurrentUserDto(Guid Id, string Username, string? DisplayName, UserRole Role, AccountStatus AccountStatus, DateTimeOffset CreatedAt, string? Email, string? ParticipantCode);
public sealed record UserDto(Guid Id, string Username, string? DisplayName, UserRole Role, AccountStatus AccountStatus, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, string? Email, string? ParticipantCode);
public sealed record CreateUserRequest(string Username, string Password, string? DisplayName, string? ParticipantCode, string? Email);
public sealed record UpdateUserRequest(string? ParticipantCode, string? DisplayName);
public sealed record SetStatusRequest(AccountStatus Status);
public sealed record SetPasswordRequest(string? Password);
public sealed record SetPasswordResponse(int SessionsEnded);

/// <summary>
/// recipeJson is null when no structured recipe is attached (the admin app shows "—"). baseName and tasks (its live
/// tile tasks, one per clay body at most) are filled on the admin recipes list only.
/// </summary>
public sealed record RecipeDto(Guid Id, string Name, string Description, string? RecipeJson, bool IsActive, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    string? Code, string? BaseCode, int? RecipeNumber, string AdditionsJson, string? OutcomeNote, string? BaseName = null, IReadOnlyList<Tasks.TileTaskDto>? Tasks = null,
    string? Cone = null, string? Atmosphere = null, string? FiringCone = null, string? FiringAtmosphere = null,
    DateTimeOffset? DeletedAt = null, DateTimeOffset? MixChangedAt = null, bool? MixEditable = null, IReadOnlyList<string>? MixLockedBecause = null);
// cone/atmosphere on a recipe: its own firing (null takes its base's). firingCone/firingAtmosphere: what its tiles are fired to, its own or its base's.
// deletedAt: when an admin deleted it (restorable). mixEditable/mixLockedBecause (one recipe only): whether its additions and
// firing can still change, and why not (a tile of it checked and approved, or photographed).
public sealed record CreateRecipeRequest(string Name, string? Description, string? RecipeJson, string? Cone = null, string? Atmosphere = null);
/// <summary>
/// cone and atmosphere: null leaves them, "" clears them. additions: a variation's colorants on top of its base, replaced
/// as a whole. Both refused (409 recipe_in_use) once a tile of it has been checked and approved, or photographed.
/// </summary>
public sealed record UpdateRecipeDetailsRequest(string? Name, string? Description, bool? IsActive, string? Cone = null, string? Atmosphere = null,
    IReadOnlyList<MaterialRow>? Additions = null);

public sealed record CaptureCodeDto(Guid Id, string? PlainCode, string MaskedSuffix, Guid AssignedUserId, string? AssignedUsername, string? LabelPrefix, bool IsActive,
    DateTimeOffset CreatedAt, DateTimeOffset? ExpiresAt, DateTimeOffset? RevokedAt, int RecipeCount);
public sealed record CreateCodeRequest(Guid AssignedUserId, string? LabelPrefix, Guid? InstructionVersionId, Guid? DefaultTileVariantId, DateTimeOffset? ExpiresAt);
public sealed record AssignRecipesRequest(IReadOnlyCollection<Guid> RecipeIds);
public sealed record CaptureContextDto(Guid CodeId, string MaskedSuffix, string? LabelPrefix, DateTimeOffset? ExpiresAt, IReadOnlyList<RecipeDto> Recipes);

/// <summary>
/// clayBody is additive: porcelain, stoneware or red_body (Ingestion:AllowedClayBodies). taskId: the tile task the photo is
/// for; the task's recipe and clay body are used, and recipeId, recipeCode and clayBody may be left out (docs/features/tile-tasks.md).
/// </summary>
/// <summary>
/// captureCodeId: the caller's capture code; it may be left out for a photo of a tile task (taskId), which anyone may
/// take once the tile is marked done. back: the back of the tile with its code, sent alongside (optional).
/// </summary>
public sealed record CreateUploadRequest(Guid? CaptureCodeId, string? FileName, string? ContentType, long SizeBytes, string? Sha256, Guid? RecipeId, string? RecipeCode, string? ClayBody = null,
    Guid? TaskId = null, UploadBackInput? Back = null);
/// <summary>The back of the tile: its own file, presigned and stored next to the front.</summary>
public sealed record UploadBackInput(string? FileName, string? ContentType, long SizeBytes);
public sealed record UploadDto(Guid Id, UploadStatus Status, string ObjectKey, string FileName, string ContentType, long DeclaredSizeBytes, long? ActualSizeBytes,
    string? FailureReason, string? ReviewNote, DateTimeOffset CreatedAt, DateTimeOffset? UploadedAt, DateTimeOffset? CompletedAt, DateTimeOffset? ReviewedAt, Uri? DownloadUrl,
    Guid? RecipeId, string? RecipeName, string? RecipeCode, Guid? ImageId, string? NextAction, Uri? ThumbnailUrl, string? ClayBody = null, Guid? TaskId = null, Uri? BackUrl = null);
/// <summary>owner: who took the photo. madeByUsername: who made the tile, when it was taken for a tile task. backUrl: the back of the tile.</summary>
public sealed record AdminUploadDto(Guid Id, Guid OwnerUserId, string? OwnerUsername, Guid? CaptureCodeId, UploadStatus Status, string ObjectKey, string FileName, string ContentType,
    long DeclaredSizeBytes, long? ActualSizeBytes, string? FailureReason, string? ReviewNote, DateTimeOffset CreatedAt, DateTimeOffset? UploadedAt, DateTimeOffset? ReviewedAt, Uri? DownloadUrl,
    Guid? RecipeId, string? RecipeName, string? RecipeCode, Guid? ImageId, QualityStatus? QualityStatus, Uri? ThumbnailUrl, string? ClayBody = null, Uri? BackUrl = null, string? MadeByUsername = null);
/// <summary>PresignDto.</summary>
/// <summary>back: a second PUT for the back of the tile, when the upload has one.</summary>
public sealed record PresignResponse(Guid UploadId, string Method, string Url, string ObjectKey, DateTimeOffset ExpiresAt, IReadOnlyDictionary<string, string> Headers,
    PresignedPart? Back = null);
public sealed record PresignedPart(string Method, string Url, string ObjectKey, DateTimeOffset ExpiresAt, IReadOnlyDictionary<string, string> Headers);
public sealed record CompleteUploadRequest(string? Sha256);
public sealed record RejectUploadRequest(string? Reason);

public sealed record OverviewDto(int TotalUsers, int ActiveCaptureCodes, int ActiveRecipes, int TotalUploads, int PendingReview);

public static class ContractValidation
{
    public static void ValidateCredentials(string username, string password)
    {
        if (string.IsNullOrWhiteSpace(username) || username.Length > 80) throw new ArgumentException("Username is required and must be at most 80 characters.");
        if (string.IsNullOrWhiteSpace(password) || password.Length < 8 || password.Length > 256) throw new ArgumentException("Password must be between 8 and 256 characters.");
    }

    public static void ValidateUpload(string fileName, string contentType, long sizeBytes, UploadOptions options)
    {
        if (string.IsNullOrWhiteSpace(fileName) || fileName.Length > 255) throw new ArgumentException("A valid file name is required.");
        if (!options.AllowedContentTypes.Contains(contentType, StringComparer.OrdinalIgnoreCase)) throw new ArgumentException("Unsupported content type.");
        if (sizeBytes <= 0 || sizeBytes > options.MaxBytes) throw new ArgumentException($"File size must be between 1 byte and {options.MaxBytes} bytes.");
    }
}

// ---- Image identity ----
public sealed record RecipeLinkResponse(Guid RecipeId, string RecipeName, string? RecipeCode, RecipeImageRelationship RelationshipType, bool Verified);
public sealed record CalculationSnapshotResponse(Guid Id, Guid RecipeId, string CalculationProfile, string CalculationVersion, string MaterialAnalysisVersion, string ChemistrySchemaVersion, string InputHash, decimal FluxTotal, string UmfJson, string WarningsJson);
public sealed record ImageAssetResponse(
    Guid ImageId, ImageSourceType SourceType, Guid? UploadId, Guid? ImportRecordId, string? ExternalRecordId, Guid? OwnerUserId,
    string TileVariantKey, string OriginalKey, string? DisplayKey, string? ThumbnailKey,
    string? Format, int? Width, int? Height, long? SizeBytes, string? Sha256,
    QualityStatus QualityStatus, string QualityReasonsJson, string? QualityPolicyVersion,
    ApprovalStatus ApprovalStatus, DateTimeOffset? ApprovedAt,
    ChemistryStatus ChemistryStatus, CalculationSnapshotResponse? Chemistry, IReadOnlyCollection<RecipeLinkResponse> Recipes,
    Guid? DuplicateOfImageId, decimal? ConeMin, decimal? ConeMax, string? Atmosphere, string LabelsJson,
    long SourceVersion, DateTimeOffset CreatedAt, DateTimeOffset? ValidatedAt, Uri? DownloadUrl,
    Uri? DisplayUrl, Uri? ThumbnailUrl, System.Text.Json.JsonElement? QualityMetrics, DateTimeOffset? DerivativesCreatedAt, string? ClayBody = null,
    Guid? DisplayCopyId = null);

public sealed record BackfillDerivativesRequest(IReadOnlyList<Guid>? ImageIds, bool? Regenerate);
public sealed record BackfillDerivativesResponse(int Enqueued);

/// <summary>
/// Records the clay body on existing images: the listed images, or every image of an import batch. With
/// onlyIfMissing (the default) images that already have a clay body keep it.
/// </summary>
/// <remarks>Exactly one of imageIds, importBatchId or participantCode (P01: the images they uploaded plus those linked to their
/// coded recipes). dryRun reports what would change without writing.</remarks>
public sealed record SetClayBodyRequest(string? ClayBody, IReadOnlyList<Guid>? ImageIds, Guid? ImportBatchId, bool? OnlyIfMissing, string? ParticipantCode = null, bool? DryRun = null);
public sealed record SetClayBodyResponse(string ClayBody, int Matched, int Updated, int ReindexQueued, bool DryRun = false);
/// <summary>kind: importBatch (key = batch id), participant (key = participant code) or all.</summary>
public sealed record ImageSourceGroup(string Kind, string Key, string Label, int Images, int WithClayBody);

/// <summary>ApproveRequest { note }; recipeId is additive (attach and verify the recipe the curator confirms).</summary>
public sealed record ApproveUploadRequest(string? Note, Guid? RecipeId);

// ---- Bulk import ----
public sealed record CreateImportBatchRequest(string Name, string? SourceLabel, string? DefaultTileVariantKey, bool AutoApprove);
public sealed record ImportBatchCounts(int Total, int Registered, int Uploaded, int Validated, int NeedsReview, int Failed, int Approved, int Indexed, int ChemistryLinked, int ChemistryMissing, int ChemistryInvalid);
public sealed record ImportBatchResponse(Guid Id, string Name, string SourceLabel, string DefaultTileVariantKey, bool AutoApprove, ImportBatchStatus Status, Guid CreatedBy, DateTimeOffset CreatedAt, DateTimeOffset? ClosedAt, ImportBatchCounts Counts);

/// <summary>One manifest line. The server generates the object key; sizeBytes/sha256 are declarations to be verified.</summary>
public sealed record ImportManifestRow(
    string ExternalRecordId, string FileName, string ContentType, long SizeBytes, string? Sha256,
    string? TileVariantKey, decimal? ConeMin, decimal? ConeMax, string? Atmosphere,
    Dictionary<string, string>? Labels, Guid? RecipeId, System.Text.Json.JsonElement? Umf, string? RecipeCode = null, string? ClayBody = null);
public sealed record RegisterImportRecordsRequest(IReadOnlyList<ImportManifestRow> Records);
public sealed record ImportRecordRegistration(string ExternalRecordId, bool Accepted, ImportRecordStatus? Status, string? ObjectKey, PresignResponse? Upload, IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings);
public sealed record RegisterImportRecordsResponse(Guid BatchId, IReadOnlyList<ImportRecordRegistration> Records);
public sealed record CompleteImportRecordsRequest(IReadOnlyList<string> ExternalRecordIds);
public sealed record ImportRecordCompletion(string ExternalRecordId, ImportRecordStatus Status, Guid? ImageId, string? Error);
public sealed record CompleteImportRecordsResponse(Guid BatchId, IReadOnlyList<ImportRecordCompletion> Records);
public sealed record ImportRecordResponse(Guid Id, string ExternalRecordId, string ObjectKey, ImportRecordStatus Status, string? FailureCode, string? FailureDetail, Guid? ImageId, QualityStatus? QualityStatus, ApprovalStatus? ApprovalStatus, ChemistryStatus? ChemistryStatus, string ChemistryWarningsJson, DateTimeOffset CreatedAt, DateTimeOffset? UploadedAt);

// ---- Lab codes: base-glaze register and coded variations ----
/// <summary>cone ("6", "06") and atmosphere (oxidation, reduction, neutral): how its tiles are fired; its variations inherit them.</summary>
public sealed record RegisterBaseGlazeRequest(string? ParticipantCode, Guid? UserId, string Name, string? Description, IReadOnlyList<MaterialRow> Formulation, string? DerivedFromBaseCode, string? ChangeNote,
    string? Cone = null, string? Atmosphere = null);
/// <summary>cone and atmosphere: only when this variation is fired differently from its base.</summary>
public sealed record RegisterVariationRequest(string Name, string? Description, IReadOnlyList<MaterialRow>? Additions, string? Cone = null, string? Atmosphere = null);
/// <summary>
/// Several variations of one base in one call (e.g. a stain at 2, 5, 7.5 and 10%). All are numbered together or none
/// is created. captureCodeId (admin only) also assigns every new recipe to that capture code.
/// </summary>
public sealed record RegisterVariationsRequest(IReadOnlyList<RegisterVariationRequest>? Variations, Guid? CaptureCodeId);
/// <summary>
/// Makes a variation the constant of a new base: base formulation + the variation's additions. Name defaults to the
/// variation's name; participantCode (admin only) puts the base in another participant's register (default: the variation's owner).
/// </summary>
public sealed record PromoteVariationRequest(string? Name, string? Description, string? ParticipantCode);
/// <summary>
/// The Register base form in one step: register a new base (<c>base</c>) or use an existing one (<c>baseCode</c>), number
/// its variations (e.g. + iron oxide at 1, 2 … 10%), and give them to a participant as tile tasks; or, with
/// <c>experiment</c>, set up its cup experiment, one stage per colorant (docs/features/cup-experiments.md). All or nothing.
/// </summary>
public sealed record RegisterBaseWithVariationsRequest(RegisterBaseGlazeRequest? Base, string? BaseCode, IReadOnlyList<RegisterVariationRequest>? Variations,
    AssignVariationsRequest? Assign, Experiments.BaseExperimentRequest? Experiment = null);
/// <summary>Who makes the new variations: one task per variation per clay body (all three when omitted), with an optional due date and instructions.</summary>
public sealed record AssignVariationsRequest(Guid AssignedUserId, IReadOnlyCollection<string>? ClayBodies, DateOnly? DueOn, string? Note);
/// <summary>The base, the variations numbered under it, the tasks made from them (null when not assigned) and its cup experiment (null when none).</summary>
public sealed record RegisterBaseWithVariationsResponse(BaseGlazeResponse Base, IReadOnlyList<RecipeDto> Variations, Tasks.AssignTileTasksResponse? Assigned,
    Experiments.ExperimentDto? Experiment = null);
/// <summary>cone and atmosphere: the variation's own firing (null leaves it, "" goes back to the base's); refused once it is in use.</summary>
public sealed record UpdateRecipeRequest(string? OutcomeNote, bool? IsActive, string? Cone = null, string? Atmosphere = null);
public sealed record BaseGlazeResponse(Guid Id, string Code, string ParticipantCode, int BaseNumber, string Name, string Description, IReadOnlyList<MaterialRow> Formulation, string? DerivedFromBaseCode, string? ChangeNote, int VariationCount, int? NextRecipeNumber, DateTimeOffset CreatedAt, IReadOnlyList<RecipeDto>? Recipes,
    string? PromotedFromRecipeCode = null, DateTimeOffset? UpdatedAt = null, bool? FormulationEditable = null, IReadOnlyList<string>? FormulationLockedBecause = null,
    string? Cone = null, string? Atmosphere = null, DateTimeOffset? DeletedAt = null);

/// <summary>Only the fields sent change. formulation replaces the whole base formulation (refused once a tile of any variation has been checked and approved, or photographed).</summary>
/// <summary>ParticipantCode gives the base to another participant, only before stage 1 starts (its code changes with it).</summary>
public sealed record UpdateBaseGlazeRequest(string? Name, string? Description, IReadOnlyList<MaterialRow>? Formulation, string? ChangeNote, string? Cone = null, string? Atmosphere = null,
    string? ParticipantCode = null);

/// <summary>
/// variationsRebuilt: variations whose full recipe was rebuilt from the new formulation. recalculated: those whose stored
/// UMF was recalculated; recalculationFailed: those that could not be (a material not in the list, say), with why.
/// </summary>
public sealed record UpdateBaseGlazeResponse(BaseGlazeResponse Base, int VariationsRebuilt, int Recalculated, IReadOnlyList<RecalculationFailure> RecalculationFailed);

public sealed record RecalculationFailure(string? RecipeCode, string Code, string Message);

// ---- Materials administration (overrides layered over materials.json) ----
/// <summary>oxideAnalysis maps oxide symbols (SiO2, Al2O3, K2O...) to weight % of the raw material.</summary>
public sealed record UpsertMaterialRequest(string Name, IReadOnlyDictionary<string, double>? OxideAnalysis, double? Loi, string? Note);
public sealed record MaterialOverrideResponse(string MaterialId, string Name, IReadOnlyDictionary<string, double> OxideAnalysis, double? Loi, string? Note,
    bool InFile, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, string MaterialsVersion);
public sealed record MaterialDuplicateEntry(int Position, string Name, IReadOnlyDictionary<string, double> OxideAnalysis, double? Loi, bool UsedByCalculator);
/// <summary>Several materials.json entries share one id; identical = same analysis (harmless). resolvedByOverride = an admin has set the analysis.</summary>
public sealed record MaterialDuplicateGroup(string MaterialId, bool Identical, bool ResolvedByOverride, IReadOnlyList<MaterialDuplicateEntry> Entries);
