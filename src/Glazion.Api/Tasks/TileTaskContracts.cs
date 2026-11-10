using Glazion.Api.Domain;
using Glazion.Api.Retrieval;

namespace Glazion.Api.Tasks;

// Shapes match @glazion/types (TileTask, TileTaskEvent, AssignTileTasks*, ReassignTileTasks*). docs/features/tile-tasks.md.

/// <summary>
/// stage: Assigned (not started), Started (being made; recipe shown), Done (made, waiting for an admin to
/// check it), AwaitingPhoto (approved: fire and photograph it; also after a photo was rejected), InReview (photo sent),
/// Approved (photo in the library), Cancelled. overdue: dueOn has passed and it isn't made yet. photo*: the latest photo
/// sent for the current assignment. version: send it back to reject stale changes. recipe: on the participant's own
/// tasks only, once they have pressed Start. returnNote: why an admin sent the made tile back (stage Started).
/// recipeChangedAt: the admin changed the recipe after the participant pressed Start (until it is approved).
/// </summary>
public sealed record TileTaskDto(
    Guid Id, Guid RecipeId, string? RecipeCode, string RecipeName, string? BaseCode, string ClayBody,
    Guid AssignedUserId, string AssignedUsername, string? AssignedDisplayName, string? ParticipantCode,
    TileTaskStage Stage, bool Overdue, DateOnly? DueOn, string? Note,
    DateTimeOffset AssignedAt, DateTimeOffset? DoneAt, DateTimeOffset? CancelledAt, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    Guid? PhotoUploadId, UploadStatus? PhotoStatus, string? RetakeReason, Guid Version,
    IReadOnlyList<TileTaskEventDto>? History = null, TaskRecipeDto? Recipe = null,
    DateTimeOffset? StartedAt = null, DateTimeOffset? ApprovedAt = null, string? ReturnNote = null, DateTimeOffset? RecipeChangedAt = null);

/// <summary>
/// A task's recipe as the participant mixes it: the base's rows, what this variation adds (the colorants, usually), and
/// the whole. Each row has its kind (colorant for a metallic oxide, stain, clay, …) so colorants can stand out. cone and
/// atmosphere: how the tile is fired, the variation's own or its base's.
/// </summary>
public sealed record TaskRecipeDto(string? Description, string? BaseCode, string? BaseName, IReadOnlyList<MatchCheckMaterial> Base,
    IReadOnlyList<MatchCheckMaterial> Additions, IReadOnlyList<MatchCheckMaterial> Materials, string? Cone = null, string? Atmosphere = null);

/// <summary>
/// The tiles of one recipe, for capture by code (GET /api/v2/capture/tiles/{code}). The recipe as written on the tile,
/// how it is fired, and one entry per tile (clay body) someone has been given.
/// </summary>
public sealed record CaptureLookupDto(Guid RecipeId, string? Code, string Name, string? BaseCode, string? BaseName, string? Cone, string? Atmosphere,
    IReadOnlyList<CaptureTileDto> Tiles);

/// <summary>
/// status: ready (made and checked by an admin: photograph it now), not_done (not made yet), not_approved (made,
/// waiting for an admin to check it), in_review (a photo is waiting for an admin), approved (its photo is in the
/// library). retakeReason: why the last photo was turned down, when it was.
/// </summary>
public sealed record CaptureTileDto(Guid TaskId, string ClayBody, TileTaskStage Stage, string Status, string MadeByUsername, bool Mine,
    DateTimeOffset? DoneAt, string? RetakeReason);

public sealed record TileTaskEventDto(TileTaskEventKind Kind, DateTimeOffset At, Guid ActorUserId, string? ActorUsername,
    Guid? FromUserId, string? FromUsername, Guid? ToUserId, string? ToUsername, string? Note);

/// <summary>
/// One thing that happened to a participant's work, newest first on their admin page (meeting 2026-11-02). kind: a task
/// event (Assigned, Started, MarkedDone, CompositionApproved, Returned, Reassigned, Cancelled, Restored...) or PhotoSent
/// (an upload they sent, with its current status). actorUsername: who did it (the participant or an admin).
/// </summary>
public sealed record ParticipantActivityDto(string Kind, DateTimeOffset At, Guid? TaskId, Guid? RecipeId, string? RecipeCode, string? RecipeName, string? ClayBody,
    string? ActorUsername, string? Note, Guid? UploadId = null, UploadStatus? UploadStatus = null);

/// <summary>Counts for the admin's stage tabs. overdue overlaps assigned and started.</summary>
public sealed record TileTaskSummaryDto(int Assigned, int Done, int InReview, int Approved, int Cancelled, int Overdue, int Started = 0, int AwaitingPhoto = 0);

/// <summary>Approve or return made tiles. reason is required to return: the participant sees it.</summary>
public sealed record ReviewTileTasksRequest(IReadOnlyCollection<Guid>? TaskIds, string? Reason);

/// <summary>updated: the tasks changed; skipped: the rest, with why (not_started, not_done, done, awaiting_photo, in_review, approved, cancelled, not_found).</summary>
public sealed record TileTaskBatchResponse(IReadOnlyList<TileTaskDto> Updated, IReadOnlyList<SkippedTaskDto> Skipped);

/// <summary>One task per recipe and clay body. clayBodies: porcelain, stoneware, red_body (all three when omitted).</summary>
public sealed record AssignTileTasksRequest(IReadOnlyCollection<Guid>? RecipeIds, IReadOnlyCollection<string>? ClayBodies, Guid AssignedUserId, DateOnly? DueOn, string? Note);

/// <summary>
/// skipped: tiles that already have a live task (with whoever has it). participantCanUpload is false when the participant
/// has no active capture code yet, so they cannot send photos until an admin issues one.
/// </summary>
public sealed record AssignTileTasksResponse(IReadOnlyList<TileTaskDto> Created, IReadOnlyList<SkippedTileDto> Skipped, bool ParticipantCanUpload);

public sealed record SkippedTileDto(Guid RecipeId, string? RecipeCode, string RecipeName, string ClayBody, Guid ExistingTaskId, Guid AssignedUserId, string AssignedUsername);

/// <summary>dueOn replaces the due date when sent. reason goes into the task's history ("did not come in").</summary>
public sealed record ReassignTileTaskRequest(Guid AssignedUserId, DateOnly? DueOn, string? Reason, Guid? Version);

public sealed record ReassignTileTasksRequest(IReadOnlyCollection<Guid>? TaskIds, Guid AssignedUserId, DateOnly? DueOn, string? Reason);

/// <summary>skipped: tasks that were not moved, with why (not_found, cancelled, in_review, approved, same_participant).</summary>
public sealed record ReassignTileTasksResponse(IReadOnlyList<TileTaskDto> Reassigned, IReadOnlyList<SkippedTaskDto> Skipped, bool ParticipantCanUpload);

public sealed record SkippedTaskDto(Guid TaskId, string Reason);

/// <summary>clearDueOn removes the due date; a blank note removes the note.</summary>
public sealed record UpdateTileTaskRequest(DateOnly? DueOn, bool? ClearDueOn, string? Note, Guid? Version);

public sealed record CancelTileTaskRequest(string? Reason, Guid? Version);
