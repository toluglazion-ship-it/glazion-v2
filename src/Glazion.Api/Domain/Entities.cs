using System.Text.Json.Serialization;

namespace Glazion.Api.Domain;

// The three account/upload enums travel as numbers: their values are part of the v2 API contract (the clients'
// @glazion/types mirrors them). Never renumber. Every other enum is operational and travels by name.
public enum UserRole { Admin = 0, User = 1 }
/// <summary>PRD AUTH-02. Pending accounts can sign in but only reach /me; suspended and disabled accounts cannot sign in.</summary>
public enum AccountStatus { Pending = 0, Active = 1, Suspended = 2, Disabled = 3 }
/// <summary>PRD UPLOAD-07 states. Stored by name, serialised by number.</summary>
public enum UploadStatus { Created = 0, AwaitingUpload = 1, Uploaded = 2, Validating = 3, NeedsReview = 4, Approved = 5, Rejected = 6, Indexing = 7, Indexed = 8, Failed = 9, Cancelled = 10 }
[JsonConverter(typeof(JsonStringEnumConverter<RecipeSourceType>))] public enum RecipeSourceType { Manual, Imported }
[JsonConverter(typeof(JsonStringEnumConverter<ImageSourceType>))] public enum ImageSourceType { UserUpload, Import }
[JsonConverter(typeof(JsonStringEnumConverter<QualityStatus>))] public enum QualityStatus { Pending, Passed, NeedsReview, Failed }
[JsonConverter(typeof(JsonStringEnumConverter<ApprovalStatus>))] public enum ApprovalStatus { Pending, Approved, Rejected, Superseded }
[JsonConverter(typeof(JsonStringEnumConverter<ChemistryStatus>))] public enum ChemistryStatus { Missing, Pending, Linked, Invalid }
/// <summary>An AI copy of a library photo: Queued and Generating while it is made; Ready to review; Approved is the one shown instead of the photo.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<AiCopyStatus>))] public enum AiCopyStatus { Queued, Generating, Ready, Failed, Approved, Rejected }
[JsonConverter(typeof(JsonStringEnumConverter<ImportBatchStatus>))] public enum ImportBatchStatus { Open, Closed }
[JsonConverter(typeof(JsonStringEnumConverter<ImportRecordStatus>))] public enum ImportRecordStatus { Registered, Uploaded, Validated, NeedsReview, Failed }
[JsonConverter(typeof(JsonStringEnumConverter<JobStatus>))] public enum JobStatus { Queued, Running, Succeeded, Failed, DeadLetter }
[JsonConverter(typeof(JsonStringEnumConverter<RecipeImageRelationship>))] public enum RecipeImageRelationship { TestTile, Reference, Variant }
[JsonConverter(typeof(JsonStringEnumConverter<IndexModality>))] public enum IndexModality { Chemistry, Text, Visual }
/// <summary>
/// Where a tile task is (docs/features/tile-tasks.md). Only Cancelled and the participant's "done" are stored; InReview and Approved
/// are read from the task's latest photo, and a rejected or failed photo puts the task back to Done (take it again).
/// </summary>
/// <summary>
/// Assigned (not started: the recipe is hidden), Started (being made; the recipe is shown), Done (made, waiting for an
/// admin to check it), AwaitingPhoto (checked and approved: fire it and photograph it, also after a rejected photo; from
/// here the recipe is locked), InReview (photo sent), Approved (photo approved), Cancelled (deleted by an admin, who can
/// restore it). Meetings 2026-10-29 and 2026-11-02.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<TileTaskStage>))] public enum TileTaskStage { Assigned, Started, Done, AwaitingPhoto, InReview, Approved, Cancelled }
[JsonConverter(typeof(JsonStringEnumConverter<TileTaskEventKind>))] public enum TileTaskEventKind { Assigned, Reassigned, MarkedDone, DoneUndone, Updated, Cancelled, Started, CompositionApproved, Returned, Restored }

public sealed class AppUser
{
    public Guid Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public UserRole Role { get; set; }
    public AccountStatus Status { get; set; } = AccountStatus.Active;
    public string? Email { get; set; }
    /// <summary>Lab identity used in every code on tiles, sheets and files: group letter + two-digit participant number, e.g. "A03". Unique; assigned by an admin.</summary>
    public string? ParticipantCode { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public ICollection<CaptureCode> AssignedCodes { get; set; } = new List<CaptureCode>();
    public ICollection<Upload> Uploads { get; set; } = new List<Upload>();
    public ICollection<BaseGlaze> BaseGlazes { get; set; } = new List<BaseGlaze>();
}

/// <summary>
/// One entry in a participant's base-glaze register, e.g. "A03-B2". The formulation can be corrected only until a tile of
/// one of its variations has been made; after that a changed base is a new base (B3) that records which base it was
/// derived from and what changed, so existing photos keep showing the recipe they were made with. Base numbers are never reused.
/// </summary>
public sealed class BaseGlaze
{
    public Guid Id { get; set; }
    public Guid OwnerUserId { get; set; }
    public AppUser OwnerUser { get; set; } = null!;
    /// <summary>Snapshot of the owner's participant code at creation, so the code stays stable even if the account changes.</summary>
    public string ParticipantCode { get; set; } = string.Empty;
    public int BaseNumber { get; set; }
    /// <summary>"{ParticipantCode}-B{BaseNumber}", e.g. "A03-B2".</summary>
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    /// <summary>Complete base formulation as material rows [{ rawName, amount, unit }]. Edited only before any variation is made (LabCodeService.UpdateBaseAsync).</summary>
    public string FormulationJson { get; set; } = "[]";
    public Guid? DerivedFromBaseId { get; set; }
    public BaseGlaze? DerivedFromBase { get; set; }
    /// <summary>What changed relative to DerivedFromBase, e.g. "Original base + 2% rutile".</summary>
    public string? ChangeNote { get; set; }
    /// <summary>
    /// The variation this base was promoted from: a tester built on that tile, so its base + additions became a new
    /// constant (A03-B1-004 → A03-B5). A variation is promoted at most once.
    /// </summary>
    public Guid? PromotedFromRecipeId { get; set; }
    public Recipe? PromotedFromRecipe { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>How its tiles are fired: cone ("6", "06") and atmosphere (oxidation, reduction, neutral). Its variations inherit them.</summary>
    public string? Cone { get; set; }
    public string? Atmosphere { get; set; }
    /// <summary>Last edit of the name, description, change note or formulation (null: never edited).</summary>
    public DateTimeOffset? UpdatedAt { get; set; }
    public Guid? UpdatedBy { get; set; }
    /// <summary>
    /// When an admin deleted it, with its variations (null: not deleted). It leaves every list but can be restored; its
    /// number stays taken, so a code written on a tile never comes to mean another glaze.
    /// </summary>
    public DateTimeOffset? DeletedAt { get; set; }
    public Guid? DeletedBy { get; set; }
    public ICollection<Recipe> Recipes { get; set; } = new List<Recipe>();
}

public sealed class UserSession
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public AppUser User { get; set; } = null!;
    /// <summary>SHA-256 of the access token. Tokens themselves are never stored.</summary>
    public string TokenHash { get; set; } = string.Empty;
    public DateTimeOffset ExpiresAt { get; set; }
    /// <summary>SHA-256 of the refresh token. Refresh rotates: the session is revoked and a new one issued.</summary>
    public string? RefreshTokenHash { get; set; }
    public DateTimeOffset? RefreshExpiresAt { get; set; }
    /// <summary>Set when a refresh replaced this session; presenting its refresh token again is treated as theft.</summary>
    public Guid? ReplacedBySessionId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? RevokedAt { get; set; }
}

/// <summary>
/// An administrative routing and labelling reference (plan §3.5), not a password. The full code is shown once at
/// creation (CAP-02); only its SHA-256 and last four characters are stored.
/// </summary>
public sealed class CaptureCode
{
    public Guid Id { get; set; }
    public string CodeHash { get; set; } = string.Empty;
    public string MaskedSuffix { get; set; } = string.Empty;
    public string? LabelPrefix { get; set; }
    public Guid AssignedUserId { get; set; }
    public AppUser AssignedUser { get; set; } = null!;
    public bool IsActive { get; set; } = true;
    public Guid? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public Guid? RevokedBy { get; set; }
    public ICollection<CodeRecipe> Recipes { get; set; } = new List<CodeRecipe>();
    public ICollection<Upload> Uploads { get; set; } = new List<Upload>();
}

public sealed class Recipe
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string RecipeJson { get; set; } = "{}";
    public RecipeSourceType SourceType { get; set; } = RecipeSourceType.Manual;
    /// <summary>Provenance pointer for imported recipes, e.g. "{batchId}:{externalRecordId}". Never user-facing.</summary>
    public string? SourceReference { get; set; }
    /// <summary>Participant who owns a coded variation; null for admin-created or imported recipes.</summary>
    public Guid? OwnerUserId { get; set; }
    /// <summary>Base this variation builds on. Set together with RecipeNumber and Code.</summary>
    public Guid? BaseGlazeId { get; set; }
    public BaseGlaze? BaseGlaze { get; set; }
    /// <summary>1..2000 within the base (shown as 001 … 999, then 1000 … 2000); never reused, even for failed tests.</summary>
    public int? RecipeNumber { get; set; }
    /// <summary>"{Participant}-B{Base}-{RecipeNumber:000}", e.g. "A03-B2-009". Unique. The short identifier written on the tile.</summary>
    public string? Code { get; set; }
    /// <summary>Additions on top of the base formulation as material rows [{ rawName, amount, unit }].</summary>
    public string AdditionsJson { get; set; } = "[]";
    /// <summary>A firing of its own, when it differs from its base's; null takes the base's (or none, for a recipe outside the register).</summary>
    public string? Cone { get; set; }
    public string? Atmosphere { get; set; }
    /// <summary>The cone the tile is fired to: its own, else its base's.</summary>
    public string? FiringCone => Cone ?? BaseGlaze?.Cone;
    /// <summary>The atmosphere the tile is fired in: its own, else its base's.</summary>
    public string? FiringAtmosphere => Atmosphere ?? BaseGlaze?.Atmosphere;
    /// <summary>Free-text outcome ("failed: crawled", "keeper") recorded against the number that stays assigned to this test.</summary>
    public string? OutcomeNote { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>
    /// When an admin last changed what goes in it (its additions, its base's materials or its firing). A participant who
    /// pressed Start before then is told the recipe changed.
    /// </summary>
    public DateTimeOffset? MixChangedAt { get; set; }
    /// <summary>When an admin deleted it (null: not deleted). Deleted recipes are inactive; restoring brings them back. The number stays taken.</summary>
    public DateTimeOffset? DeletedAt { get; set; }
    public Guid? DeletedBy { get; set; }
    public ICollection<CodeRecipe> Codes { get; set; } = new List<CodeRecipe>();
    public ICollection<RecipeCalculationResult> CalculationResults { get; set; } = new List<RecipeCalculationResult>();
    public ICollection<RecipeImage> Images { get; set; } = new List<RecipeImage>();
}

public sealed class CodeRecipe
{
    public Guid CaptureCodeId { get; set; }
    public CaptureCode CaptureCode { get; set; } = null!;
    public Guid RecipeId { get; set; }
    public Recipe Recipe { get; set; } = null!;
    public DateTimeOffset AssignedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class Upload
{
    public Guid Id { get; set; }
    /// <summary>Who took the photo: the tile's maker, or anyone who photographed a tile marked done by its code.</summary>
    public Guid OwnerUserId { get; set; }
    public AppUser OwnerUser { get; set; } = null!;
    /// <summary>The capture code it was sent under; null for a photo of a tile task taken by its code (meeting 2026-10-29).</summary>
    public Guid? CaptureCodeId { get; set; }
    public CaptureCode? CaptureCode { get; set; }
    /// <summary>Recipe the user declared at capture (by lab code). Resolved and ownership-checked by the API; the image is linked to it on completion.</summary>
    public Guid? RecipeId { get; set; }
    public Recipe? Recipe { get; set; }
    /// <summary>Clay body the tile was made from, as declared at capture (Ingestion:AllowedClayBodies); copied onto the image.</summary>
    public string? ClayBody { get; set; }
    public string ObjectKey { get; set; } = string.Empty;
    /// <summary>The back of the tile, with its code written on it: proof of which tile it is. Kept with the photo, never indexed.</summary>
    public string? BackObjectKey { get; set; }
    public string? BackContentType { get; set; }
    public long? BackDeclaredSizeBytes { get; set; }
    public string OriginalFileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public long DeclaredSizeBytes { get; set; }
    public string? DeclaredSha256 { get; set; }
    public long? ActualSizeBytes { get; set; }
    public string? ETag { get; set; }
    public UploadStatus Status { get; set; } = UploadStatus.Created;
    /// <summary>Why processing failed (validation, quarantine). Review decisions go in ReviewNote.</summary>
    public string? FailureReason { get; set; }
    /// <summary>The reviewer's note: the reason on rejection, the optional note on approval.</summary>
    public string? ReviewNote { get; set; }
    public DateTimeOffset? ReviewedAt { get; set; }
    public Guid? ReviewedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UploadedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public Guid ConcurrencyStamp { get; set; } = Guid.NewGuid();
    public ImageAsset? ImageAsset { get; set; }
    /// <summary>The tile task this photo was taken for, when it was taken from one (or matched one: same recipe, clay body and participant).</summary>
    public Guid? TileTaskId { get; set; }
    public TileTask? TileTask { get; set; }
}

/// <summary>
/// One test tile to make: a recipe on one clay body, given to one participant, usually with a due date
/// (docs/features/tile-tasks.md). The participant says when the tile is made and photographs it; what happens to the photo
/// (in review, approved, rejected) is read from the upload, never copied here, so the task cannot drift from review.
/// A recipe has at most one live (not cancelled) task per clay body.
/// </summary>
public sealed class TileTask
{
    public Guid Id { get; set; }
    public Guid RecipeId { get; set; }
    public Recipe Recipe { get; set; } = null!;
    /// <summary>porcelain, stoneware or red_body (Ingestion:AllowedClayBodies).</summary>
    public string ClayBody { get; set; } = string.Empty;
    /// <summary>The participant making the tile now. Reassigning changes it; the recipe's own code never changes.</summary>
    public Guid AssignedUserId { get; set; }
    public AppUser AssignedUser { get; set; } = null!;
    /// <summary>When the current participant got the task. Photos sent before then belong to an earlier assignment.</summary>
    public DateTimeOffset AssignedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateOnly? DueOn { get; set; }
    /// <summary>The admin's instructions for the participant.</summary>
    public string? Note { get; set; }
    /// <summary>When the current participant pressed Start: from then the recipe is shown to them. Cleared on reassignment.</summary>
    public DateTimeOffset? StartedAt { get; set; }
    /// <summary>When the current participant said the tile is made. Cleared on reassignment and when an admin returns it.</summary>
    public DateTimeOffset? DoneAt { get; set; }
    /// <summary>When an admin checked the made tile and approved it for its photo. Only then can it be photographed.</summary>
    public DateTimeOffset? ApprovedAt { get; set; }
    public Guid? ApprovedBy { get; set; }
    /// <summary>Why an admin sent the made tile back (shown to the participant until they mark it done again).</summary>
    public string? ReturnNote { get; set; }
    public DateTimeOffset? CancelledAt { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>Changes on every write; clients send it back as version so a stale screen cannot overwrite a newer change.</summary>
    public Guid ConcurrencyStamp { get; set; } = Guid.NewGuid();
    public ICollection<Upload> Uploads { get; set; } = new List<Upload>();
    public ICollection<TileTaskEvent> Events { get; set; } = new List<TileTaskEvent>();
}

/// <summary>A task's history: who assigned, reassigned, marked done or cancelled it, and when.</summary>
public sealed class TileTaskEvent
{
    public Guid Id { get; set; }
    public Guid TileTaskId { get; set; }
    public TileTask TileTask { get; set; } = null!;
    public TileTaskEventKind Kind { get; set; }
    public Guid ActorUserId { get; set; }
    /// <summary>Previous participant, on a reassignment.</summary>
    public Guid? FromUserId { get; set; }
    /// <summary>The participant it went to, on an assignment or reassignment.</summary>
    public Guid? ToUserId { get; set; }
    public string? Note { get; set; }
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Immutable UMF/chemistry snapshot for a recipe. A new calculation (different profile, versions, or inputs)
/// always produces a new row; rows are never updated in place so an indexed image can pin exactly what it used.
/// </summary>
public sealed class RecipeCalculationResult
{
    public Guid Id { get; set; }
    public Guid RecipeId { get; set; }
    public Recipe Recipe { get; set; } = null!;
    public string CalculationProfile { get; set; } = string.Empty;
    public string CalculationVersion { get; set; } = string.Empty;
    public string MaterialAnalysisVersion { get; set; } = string.Empty;
    public string ChemistrySchemaVersion { get; set; } = string.Empty;
    /// <summary>SHA-256 of the canonical input. Same input + same versions = same hash.</summary>
    public string InputHash { get; set; } = string.Empty;
    public string OxideWeightPercentJson { get; set; } = "{}";
    public string RawMolesJson { get; set; } = "{}";
    public decimal FluxTotal { get; set; }
    public string UmfJson { get; set; } = "{}";
    public string OxideGroupsJson { get; set; } = "{}";
    public string RatiosJson { get; set; } = "{}";
    public string WarningsJson { get; set; } = "[]";
    /// <summary>The rows and scaled lines the snapshot was calculated from, so it can be recomputed and audited.</summary>
    public string InputJson { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class RecipeImage
{
    public Guid RecipeId { get; set; }
    public Recipe Recipe { get; set; } = null!;
    public Guid ImageId { get; set; }
    public ImageAsset Image { get; set; } = null!;
    public RecipeImageRelationship RelationshipType { get; set; } = RecipeImageRelationship.TestTile;
    public bool Verified { get; set; }
    public Guid? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// The canonical image identity. Everything downstream (labels, vectors, search results, feedback) references Id.
/// Exactly one of UploadId / ImportRecordId is set.
/// </summary>
public sealed class ImageAsset
{
    public Guid Id { get; set; }
    public ImageSourceType SourceType { get; set; }
    public Guid? UploadId { get; set; }
    public Upload? Upload { get; set; }
    public Guid? ImportRecordId { get; set; }
    public ImportRecord? ImportRecord { get; set; }
    public Guid? OwnerUserId { get; set; }
    public string TileVariantKey { get; set; } = "white";
    public string OriginalKey { get; set; } = string.Empty;
    /// <summary>The back of the tile (its code), from the upload. Shown beside the photo; search and the index use the front only.</summary>
    public string? BackKey { get; set; }
    public string? BackContentType { get; set; }
    public string? DisplayKey { get; set; }
    public string? ThumbnailKey { get; set; }
    /// <summary>Pixel-level quality measurements (sharpness, exposure, upright size) from the derivative pass; null until it runs.</summary>
    public string? QualityMetricsJson { get; set; }
    public DateTimeOffset? DerivativesCreatedAt { get; set; }
    public string DeclaredContentType { get; set; } = string.Empty;
    public long DeclaredSizeBytes { get; set; }
    public string? DeclaredSha256 { get; set; }
    public string? Format { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public long? SizeBytes { get; set; }
    public string? Sha256 { get; set; }
    public string? ETag { get; set; }
    public QualityStatus QualityStatus { get; set; } = QualityStatus.Pending;
    public string QualityReasonsJson { get; set; } = "[]";
    public string? QualityPolicyVersion { get; set; }
    public ApprovalStatus ApprovalStatus { get; set; } = ApprovalStatus.Pending;
    public DateTimeOffset? ApprovedAt { get; set; }
    public Guid? ApprovedBy { get; set; }
    public ChemistryStatus ChemistryStatus { get; set; } = ChemistryStatus.Missing;
    public Guid? CalculationResultId { get; set; }
    public RecipeCalculationResult? CalculationResult { get; set; }
    public Guid? DuplicateOfImageId { get; set; }
    public decimal? ConeMin { get; set; }
    public decimal? ConeMax { get; set; }
    public string? Atmosphere { get; set; }
    /// <summary>
    /// Clay body under the glaze ("porcelain", "stoneware", "red_body"). The same recipe looks different on each, so
    /// every tile records it; null means not recorded (images captured before 2026-10-13).
    /// </summary>
    public string? ClayBody { get; set; }
    /// <summary>Controlled labels supplied at ingestion (colourFamily, surface, opacity...). Proposed, not approved, until review.</summary>
    public string LabelsJson { get; set; } = "{}";
    /// <summary>Increments on every label/recipe/approval change. Indexing supersedes an old vector only after a newer SourceVersion is live.</summary>
    public long SourceVersion { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ValidatedAt { get; set; }
    public Guid ConcurrencyStamp { get; set; } = Guid.NewGuid();
    public ICollection<RecipeImage> Recipes { get; set; } = new List<RecipeImage>();
    /// <summary>
    /// The approved AI copy shown in place of this photo (DisplayKey and ThumbnailKey then point at the copy's files;
    /// the original and its own derivatives stay where they are). Null: the photo itself is shown.
    /// </summary>
    public Guid? DisplayCopyId { get; set; }
}

/// <summary>
/// A copy of a library photo made with an image model from the photo itself (docs/features/ai-copies.md): the same glaze,
/// photographed afresh, so the corpus's photos can be shown without showing someone else's picture. An admin compares
/// it with the original and approves it, or makes another.
/// </summary>
public sealed class AiImageCopy
{
    public Guid Id { get; set; }
    public Guid ImageId { get; set; }
    public ImageAsset Image { get; set; } = null!;
    public AiCopyStatus Status { get; set; } = AiCopyStatus.Queued;
    public string Model { get; set; } = string.Empty;
    public string Quality { get; set; } = string.Empty;
    /// <summary>The whole prompt sent, so a copy can be traced to exactly what was asked.</summary>
    public string Prompt { get; set; } = string.Empty;
    /// <summary>What the admin added for this copy, e.g. "less shine", "keep the speckles".</summary>
    public string? Instruction { get; set; }
    /// <summary>The copy at display size (WebP), and its thumbnail. Set once it is Ready.</summary>
    public string? Key { get; set; }
    public string? ThumbnailKey { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    /// <summary>The model's token usage, as it reported it (what the copy cost).</summary>
    public string? UsageJson { get; set; }
    public string? Error { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
    public Guid? ReviewedBy { get; set; }
    public DateTimeOffset? ReviewedAt { get; set; }
}

public sealed class ImportBatch
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string SourceLabel { get; set; } = "curated_dataset";
    public string DefaultTileVariantKey { get; set; } = "white";
    /// <summary>When true the worker approves images that pass validation cleanly. Never approves NeedsReview. Default false (explicit policy per batch).</summary>
    public bool AutoApprove { get; set; }
    public ImportBatchStatus Status { get; set; } = ImportBatchStatus.Open;
    public Guid CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ClosedAt { get; set; }
    public ICollection<ImportRecord> Records { get; set; } = new List<ImportRecord>();
}

public sealed class ImportRecord
{
    public Guid Id { get; set; }
    public Guid BatchId { get; set; }
    public ImportBatch Batch { get; set; } = null!;
    public string ExternalRecordId { get; set; } = string.Empty;
    public string ObjectKey { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string DeclaredContentType { get; set; } = string.Empty;
    public long DeclaredSizeBytes { get; set; }
    public string? DeclaredSha256 { get; set; }
    public string TileVariantKey { get; set; } = "white";
    public decimal? ConeMin { get; set; }
    public decimal? ConeMax { get; set; }
    public string? Atmosphere { get; set; }
    public string? ClayBody { get; set; }
    public string LabelsJson { get; set; } = "{}";
    public Guid? RecipeId { get; set; }
    public string? UmfJson { get; set; }
    public string ChemistryWarningsJson { get; set; } = "[]";
    public ImportRecordStatus Status { get; set; } = ImportRecordStatus.Registered;
    public string? FailureCode { get; set; }
    public string? FailureDetail { get; set; }
    public ImageAsset? ImageAsset { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UploadedAt { get; set; }
}

public sealed class ProcessingJob
{
    public Guid Id { get; set; }
    public string JobType { get; set; } = string.Empty;
    public string EntityType { get; set; } = string.Empty;
    public Guid EntityId { get; set; }
    public JobStatus Status { get; set; } = JobStatus.Queued;
    public int Attempts { get; set; }
    public int MaxAttempts { get; set; } = 5;
    public string DedupeKey { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = "{}";
    public string? ErrorCode { get; set; }
    public string? ErrorDetail { get; set; }
    public DateTimeOffset AvailableAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? LeaseExpiresAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// The immutable-ish manifest of what is in the vector index for one entity, one modality, one embedding version.
/// Written only after the vector provider confirms success (plan §12.3). A superseded row keeps its history; the
/// vector it points at has already been deleted.
/// </summary>
public sealed class IndexRecord
{
    public Guid Id { get; set; }
    public string EntityType { get; set; } = "image";
    public Guid EntityId { get; set; }
    public long SourceVersion { get; set; }
    public bool Indexable { get; set; }
    public ApprovalStatus ApprovalStatus { get; set; }
    public IndexModality Modality { get; set; }
    public string? ChemistrySchemaVersion { get; set; }
    public string? ChemistryVectorHash { get; set; }
    public string? CanonicalTextHash { get; set; }
    public string? ImageSha256 { get; set; }
    public string EmbeddingModel { get; set; } = string.Empty;
    public string EmbeddingVersion { get; set; } = string.Empty;
    public string VectorNamespace { get; set; } = string.Empty;
    public string VectorId { get; set; } = string.Empty;
    public string MetadataJson { get; set; } = "{}";
    /// <summary>The vector store that confirmed this record ("InMemory", "Pinecone"). After a switch, records from another store are re-sent.</summary>
    public string? VectorProvider { get; set; }
    public DateTimeOffset? IndexedAt { get; set; }
    public DateTimeOffset? SupersededAt { get; set; }
    public string? LastErrorCode { get; set; }
    public string? LastErrorDetail { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// An administrator's correction or addition to the materials list, layered over materials.json at load time. Keyed by
/// the material id (the name's slug): when the file has entries with that id, every one of them takes this analysis
/// (which settles the file's conflicting duplicates); otherwise it is a new material. Deleting the row reverts to the file.
/// </summary>
public sealed class MaterialOverride
{
    public Guid Id { get; set; }
    /// <summary>Slug of Name, e.g. "cp-7-ball-clay". Unique.</summary>
    public string MaterialId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    /// <summary>Oxide symbol → weight % of the raw material, e.g. {"SiO2": 68.5, "Al2O3": 18.2}.</summary>
    public string OxideAnalysisJson { get; set; } = "{}";
    public double? Loi { get; set; }
    /// <summary>Why it was changed or where the analysis came from (supplier sheet, lab test).</summary>
    public string? Note { get; set; }
    public Guid CreatedBy { get; set; }
    public Guid UpdatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>One executed recipe-to-image search with the exact inputs, versions, and ranked results used at query time (plan §5.8).</summary>
public sealed class SearchRun
{
    public Guid Id { get; set; }
    public Guid RequesterUserId { get; set; }
    /// <summary>"umf" or "recipe". Raw recipes are canonicalised to one of these or rejected before a run exists.</summary>
    public string QueryType { get; set; } = string.Empty;
    /// <summary>SHA-256 of the canonical query (chemistry, firing, filters, topK).</summary>
    public string InputHash { get; set; } = string.Empty;
    public string? IdempotencyKey { get; set; }
    /// <summary>Canonical query plus every version that influenced ranking (schema, embedding, ranker, weights, thresholds).</summary>
    public string QuerySnapshotJson { get; set; } = "{}";
    public string FiltersJson { get; set; } = "{}";
    public int CandidateCount { get; set; }
    public int ResultCount { get; set; }
    public string RankerVersion { get; set; } = string.Empty;
    public int? LatencyMs { get; set; }
    /// <summary>Ranked items as returned, without signed URLs (those are re-issued on read).</summary>
    public string ResultsJson { get; set; } = "[]";
    public string WarningsJson { get; set; } = "[]";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Relevance judgement on one result. Evaluation data only: it never changes ranker weights or labels (R2I-08).</summary>
public sealed class SearchFeedback
{
    public Guid Id { get; set; }
    public Guid SearchRunId { get; set; }
    public SearchRun SearchRun { get; set; } = null!;
    public Guid ImageId { get; set; }
    public Guid RequesterUserId { get; set; }
    public string Feedback { get; set; } = string.Empty;
    public string? Note { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

// ---- Chat (PRD CHAT-01..06): conversations are private to their owner ----

public sealed class Conversation
{
    public Guid Id { get; set; }
    public Guid OwnerUserId { get; set; }
    public string Title { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<ChatMessage> Messages { get; set; } = [];
}

public sealed class ChatMessage
{
    public Guid Id { get; set; }
    public Guid ConversationId { get; set; }
    public Conversation Conversation { get; set; } = null!;
    /// <summary>"user" or "assistant", as the apps' ChatMessage.role.</summary>
    public string Role { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    /// <summary>ChatSource[] shown under an answer.</summary>
    public string SourcesJson { get; set; } = "[]";
    /// <summary>ChatToolCall[] with one user-facing line per tool the answer used.</summary>
    public string ToolCallsJson { get; set; } = "[]";
    /// <summary>"high", "medium" or "low" for assistant messages.</summary>
    public string? Confidence { get; set; }
    /// <summary>Model that wrote an assistant message, for audits and evaluations.</summary>
    public string? Model { get; set; }
    /// <summary>What the model was told with a user message and the conversation never shows: the tile it was asked from.</summary>
    public string? Context { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class ChatFeedback
{
    public Guid Id { get; set; }
    public Guid MessageId { get; set; }
    public ChatMessage Message { get; set; } = null!;
    public Guid UserId { get; set; }
    /// <summary>"helpful" or "not_helpful".</summary>
    public string Feedback { get; set; } = string.Empty;
    public string? Note { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// A setting an admin changes from the dashboard, by key (e.g. "search.strictFiring"). A missing row means the default,
/// so a new setting needs no migration of its own.
/// </summary>
public sealed class AppSetting
{
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public Guid? UpdatedBy { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// A cup experiment (meeting 2026-11-02, "Glaze Research Matrix"): one participant mixes a base glaze into CupCount cups
/// and adds materials in stages, dipping tiles after every change, so a little glaze yields many recipes. Stage 1 varies
/// by cup; later stages are constant (one step) or variable (one step per level). Every step makes one recipe per cup,
/// and its dipped tiles wait for an admin's check like any other. Amounts are % of the dry base still in the cup, which
/// Glazion tracks from the weight the participant enters after each dip (ExperimentMath).
/// </summary>
public sealed class Experiment
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public Guid BaseGlazeId { get; set; }
    public BaseGlaze BaseGlaze { get; set; } = null!;
    public Guid ParticipantUserId { get; set; }
    public AppUser Participant { get; set; } = null!;
    public int CupCount { get; set; }
    /// <summary>Dry base glaze and water put in each cup at the start, in grams (100 and 60 by default).</summary>
    public double DryGrams { get; set; }
    public double WaterGrams { get; set; }
    /// <summary>Tiles dipped after every change, and the clay bodies they are (one tile task per distinct clay body).</summary>
    public int TilesPerStep { get; set; }
    public string ClayBodiesJson { get; set; } = "[]";
    /// <summary>A cup with less wet glaze than this after the next dip asks for fresh glaze.</summary>
    public double MinReserveGrams { get; set; }
    /// <summary>The stages as JSON (ExperimentStage). A stage can change until its first step is started.</summary>
    public string StagesJson { get; set; } = "[]";
    /// <summary>Each cup now (ExperimentCup): what is in it, in grams.</summary>
    public string CupsJson { get; set; } = "[]";
    public Guid CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? DeletedAt { get; set; }
    public ICollection<ExperimentStep> Steps { get; set; } = new List<ExperimentStep>();
}

/// <summary>
/// What happened in an experiment, in order: a mix step (materials added to every cup, then tiles dipped and one cup
/// weighed) or a refill (fresh glaze added to a cup that ran low, with the earlier materials).
/// </summary>
public sealed class ExperimentStep
{
    public Guid Id { get; set; }
    public Guid ExperimentId { get; set; }
    public Experiment Experiment { get; set; } = null!;
    public int Ordinal { get; set; }
    /// <summary>"mix" or "refill".</summary>
    public string Kind { get; set; } = "mix";
    /// <summary>Mix steps: which stage (0-based) and level (1-based).</summary>
    public int? StageIndex { get; set; }
    public int? Level { get; set; }
    public string? Material { get; set; }
    /// <summary>Per cup: the amounts of this step (mix: target %, grams added, recipe; refill: fresh glaze and grams of each material).</summary>
    public string CupsJson { get; set; } = "[]";
    public Guid ActorUserId { get; set; }
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>Mix steps: when the tiles were dipped and which cup was weighed after.</summary>
    public DateTimeOffset? DippedAt { get; set; }
    public int? WeighedCup { get; set; }
    public double? WeighedGrams { get; set; }
    public double? Retention { get; set; }
}
