using Glazion.Api.Contracts;
using Glazion.Api.Controllers;
using Glazion.Api.Data;
using Glazion.Api.Domain;
using Glazion.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Glazion.Api.Tasks;

/// <summary>
/// Tile tasks (docs/features/tile-tasks.md): an admin gives a participant a recipe to make on a clay body, usually by a date. The
/// participant presses Start (which shows them the recipe and locks it), makes the tile and marks it done; an admin
/// checks it and approves it for its photo (or returns it); once fired, anyone photographs it by its code, and the photo
/// goes through the usual review. Reassigning moves a task not yet made to another participant, keeping its history.
/// </summary>
public sealed class TileTaskService(GlazionDbContext db, IOptions<IngestionOptions> ingestion, ILogger<TileTaskService> logger)
{
    public const int MaxRecipesPerAssign = 200;
    public const int MaxTasksPerReassign = 500;
    public const int MaxNoteLength = 500;

    /// <summary>Photos that count towards a task: sent to the API, not only created or withdrawn.</summary>
    internal static readonly UploadStatus[] Sent =
    [
        UploadStatus.Uploaded, UploadStatus.Validating, UploadStatus.NeedsReview, UploadStatus.Approved, UploadStatus.Rejected,
        UploadStatus.Indexing, UploadStatus.Indexed, UploadStatus.Failed
    ];
    internal static readonly UploadStatus[] Reviewing = [UploadStatus.Uploaded, UploadStatus.Validating, UploadStatus.NeedsReview];
    internal static readonly UploadStatus[] Accepted = [UploadStatus.Approved, UploadStatus.Indexing, UploadStatus.Indexed];
    /// <summary>A rejected photo, or one that failed the quality checks, has to be taken again.</summary>
    internal static readonly UploadStatus[] Retake = [UploadStatus.Rejected, UploadStatus.Failed];

    /// <summary>A task with the status of the latest photo sent for its current assignment (null: none yet).</summary>
    public sealed class Row
    {
        public TileTask Task { get; init; } = null!;
        public UploadStatus? Photo { get; init; }
    }

    public static IQueryable<Row> Rows(IQueryable<TileTask> tasks) => tasks.Select(t => new Row
    {
        Task = t,
        Photo = t.Uploads.Where(u => u.CreatedAt >= t.AssignedAt && Sent.Contains(u.Status))
            .OrderByDescending(u => u.CreatedAt).Select(u => (UploadStatus?)u.Status).FirstOrDefault()
    });

    public static IQueryable<Row> WhereStage(IQueryable<Row> rows, TileTaskStage stage) => stage switch
    {
        TileTaskStage.Cancelled => rows.Where(r => r.Task.CancelledAt != null),
        TileTaskStage.Approved => rows.Where(r => r.Task.CancelledAt == null && r.Photo != null && Accepted.Contains(r.Photo.Value)),
        TileTaskStage.InReview => rows.Where(r => r.Task.CancelledAt == null && r.Photo != null && Reviewing.Contains(r.Photo.Value)),
        TileTaskStage.AwaitingPhoto => rows.Where(r => r.Task.CancelledAt == null && (r.Photo == null ? r.Task.ApprovedAt != null : Retake.Contains(r.Photo.Value))),
        TileTaskStage.Done => rows.Where(r => r.Task.CancelledAt == null && r.Photo == null && r.Task.ApprovedAt == null && r.Task.DoneAt != null),
        TileTaskStage.Started => WhereNotMade(rows).Where(r => r.Task.StartedAt != null),
        _ => WhereNotMade(rows).Where(r => r.Task.StartedAt == null)
    };

    /// <summary>Still with the participant to make: not started, or started and not yet marked done.</summary>
    public static IQueryable<Row> WhereNotMade(IQueryable<Row> rows) =>
        rows.Where(r => r.Task.CancelledAt == null && r.Photo == null && r.Task.ApprovedAt == null && r.Task.DoneAt == null);

    public static IQueryable<Row> WhereOverdue(IQueryable<Row> rows, DateOnly today) =>
        WhereNotMade(rows).Where(r => r.Task.DueOn != null && r.Task.DueOn < today);

    public static TileTaskStage StageOf(TileTask task, UploadStatus? photo) =>
        task.CancelledAt is not null ? TileTaskStage.Cancelled
        : photo is { } p && Accepted.Contains(p) ? TileTaskStage.Approved
        : photo is { } q && Reviewing.Contains(q) ? TileTaskStage.InReview
        : photo is not null || task.ApprovedAt is not null ? TileTaskStage.AwaitingPhoto
        : task.DoneAt is not null ? TileTaskStage.Done
        : task.StartedAt is not null ? TileTaskStage.Started
        : TileTaskStage.Assigned;

    /// <summary>The participant's own list order: to make first, then ready to photograph, waiting for a check, in review, approved.</summary>
    public static IQueryable<Row> OrderedForParticipant(IQueryable<Row> rows) => rows
        .OrderBy(r => r.Photo != null ? (Retake.Contains(r.Photo.Value) ? 1 : Reviewing.Contains(r.Photo.Value) ? 3 : 4)
            : r.Task.ApprovedAt != null ? 1 : r.Task.DoneAt != null ? 2 : 0)
        .ThenBy(r => r.Task.DueOn == null).ThenBy(r => r.Task.DueOn).ThenBy(r => r.Task.CreatedAt).ThenBy(r => r.Task.Id);

    public static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);

    // ---- reading ----

    /// <summary>The order both lists use: soonest due first (no date last), then oldest first.</summary>
    public static IOrderedQueryable<Row> Ordered(IQueryable<Row> rows) =>
        rows.OrderBy(r => r.Task.DueOn == null).ThenBy(r => r.Task.DueOn).ThenBy(r => r.Task.CreatedAt).ThenBy(r => r.Task.Id);

    public async Task<TileTaskSummaryDto> SummaryAsync(IQueryable<TileTask> scope, CancellationToken cancellationToken)
    {
        var rows = Rows(scope);
        return new TileTaskSummaryDto(
            await WhereStage(rows, TileTaskStage.Assigned).CountAsync(cancellationToken),
            await WhereStage(rows, TileTaskStage.Done).CountAsync(cancellationToken),
            await WhereStage(rows, TileTaskStage.InReview).CountAsync(cancellationToken),
            await WhereStage(rows, TileTaskStage.Approved).CountAsync(cancellationToken),
            await WhereStage(rows, TileTaskStage.Cancelled).CountAsync(cancellationToken),
            await WhereOverdue(rows, Today).CountAsync(cancellationToken),
            await WhereStage(rows, TileTaskStage.Started).CountAsync(cancellationToken),
            await WhereStage(rows, TileTaskStage.AwaitingPhoto).CountAsync(cancellationToken));
    }

    public async Task<TileTaskDto> GetAsync(Guid taskId, Guid? assignedTo, bool withHistory, CancellationToken cancellationToken)
    {
        var scope = db.TileTasks.AsNoTracking().Where(t => t.Id == taskId);
        if (assignedTo is { } userId) scope = scope.Where(t => t.AssignedUserId == userId && t.CancelledAt == null);
        var row = await Rows(scope).SingleOrDefaultAsync(cancellationToken) ?? throw TaskNotFound();
        return (await MapAsync([row], withHistory, cancellationToken))[0];
    }

    /// <summary>DTOs for a page of rows: recipe, participant and latest photo looked up in three queries, not per row.</summary>
    public async Task<IReadOnlyList<TileTaskDto>> MapAsync(IReadOnlyList<Row> rows, bool withHistory, CancellationToken cancellationToken)
    {
        if (rows.Count == 0) return [];
        var taskIds = rows.Select(r => r.Task.Id).ToList();
        var recipeIds = rows.Select(r => r.Task.RecipeId).Distinct().ToList();
        var recipes = await db.Recipes.AsNoTracking().Include(x => x.BaseGlaze).Where(x => recipeIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, cancellationToken);
        var photos = (await db.Uploads.AsNoTracking().Where(u => u.TileTaskId != null && taskIds.Contains(u.TileTaskId.Value) && Sent.Contains(u.Status))
                .Select(u => new { u.Id, TaskId = u.TileTaskId!.Value, u.Status, u.CreatedAt, u.ReviewNote, u.FailureReason }).ToListAsync(cancellationToken))
            .GroupBy(u => u.TaskId).ToDictionary(g => g.Key, g => g.OrderByDescending(u => u.CreatedAt).ToList());
        var events = withHistory
            ? await db.TileTaskEvents.AsNoTracking().Where(e => taskIds.Contains(e.TileTaskId)).OrderBy(e => e.At).ToListAsync(cancellationToken)
            : [];
        var userIds = rows.Select(r => r.Task.AssignedUserId)
            .Concat(events.SelectMany(e => new[] { (Guid?)e.ActorUserId, e.FromUserId, e.ToUserId }).OfType<Guid>()).Distinct().ToList();
        var users = await db.Users.AsNoTracking().Where(x => userIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, cancellationToken);
        string? Name(Guid? id) => id is { } v && users.TryGetValue(v, out var u) ? u.Username : null;

        var today = Today;
        return rows.Select(row =>
        {
            var t = row.Task;
            var recipe = recipes.GetValueOrDefault(t.RecipeId);
            var user = users.GetValueOrDefault(t.AssignedUserId);
            var photo = photos.GetValueOrDefault(t.Id)?.FirstOrDefault(p => p.CreatedAt >= t.AssignedAt);
            var stage = StageOf(t, row.Photo);
            var retake = stage == TileTaskStage.AwaitingPhoto && photo is not null && Retake.Contains(photo.Status)
                ? photo.Status == UploadStatus.Rejected ? photo.ReviewNote ?? "The photo was rejected." : photo.FailureReason ?? "The photo did not pass the quality checks."
                : null;
            var history = withHistory
                ? events.Where(e => e.TileTaskId == t.Id).Select(e => new TileTaskEventDto(e.Kind, e.At, e.ActorUserId, Name(e.ActorUserId), e.FromUserId, Name(e.FromUserId), e.ToUserId, Name(e.ToUserId), e.Note)).ToList()
                : null;
            return new TileTaskDto(t.Id, t.RecipeId, recipe?.Code, recipe?.Name ?? "Recipe", recipe?.BaseGlaze?.Code, t.ClayBody,
                t.AssignedUserId, user?.Username ?? "unknown", string.IsNullOrWhiteSpace(user?.DisplayName) ? null : user.DisplayName, user?.ParticipantCode,
                stage, stage is TileTaskStage.Assigned or TileTaskStage.Started && t.DueOn < today, t.DueOn, t.Note,
                t.AssignedAt, t.DoneAt, t.CancelledAt, t.CreatedAt, t.UpdatedAt,
                photo?.Id, photo?.Status, retake, t.ConcurrencyStamp, history,
                StartedAt: t.StartedAt, ApprovedAt: t.ApprovedAt, ReturnNote: stage == TileTaskStage.Started ? t.ReturnNote : null,
                // The admin changed the recipe after this participant pressed Start: they should check the new amounts.
                RecipeChangedAt: t.StartedAt is { } started && recipe?.MixChangedAt is { } changed && changed > started && t.ApprovedAt is null ? changed : null);
        }).ToList();
    }

    // ---- admin ----

    public async Task<AssignTileTasksResponse> AssignAsync(AssignTileTasksRequest request, Guid adminId, CancellationToken cancellationToken)
    {
        var recipeIds = (request.RecipeIds ?? []).Distinct().ToList();
        if (recipeIds.Count == 0) throw ApiProblems.Validation("recipeIds", "Choose at least one recipe.");
        if (recipeIds.Count > MaxRecipesPerAssign) throw ApiProblems.Validation("recipeIds", $"Assign at most {MaxRecipesPerAssign} recipes at a time.");
        var clayBodies = ClayBodies(request.ClayBodies);
        var dueOn = DueOn(request.DueOn, "dueOn");
        var note = Note(request.Note, "note");
        var participant = await ParticipantAsync(request.AssignedUserId, cancellationToken);

        var recipes = await db.Recipes.AsNoTracking().Where(x => recipeIds.Contains(x.Id) && x.IsActive).ToListAsync(cancellationToken);
        if (recipes.Count != recipeIds.Count) throw ApiProblems.Validation("recipeIds", "One or more recipes do not exist or are not active.");
        var live = await db.TileTasks.AsNoTracking().Include(t => t.AssignedUser).Where(t => recipeIds.Contains(t.RecipeId) && t.CancelledAt == null)
            .ToDictionaryAsync(t => (t.RecipeId, t.ClayBody), cancellationToken);

        var now = DateTimeOffset.UtcNow;
        var created = new List<TileTask>();
        var skipped = new List<SkippedTileDto>();
        foreach (var recipe in recipes.OrderBy(r => r.Code ?? r.Name, StringComparer.Ordinal))
        foreach (var clayBody in clayBodies)
        {
            if (live.TryGetValue((recipe.Id, clayBody), out var existing))
            {
                skipped.Add(new SkippedTileDto(recipe.Id, recipe.Code, recipe.Name, clayBody, existing.Id, existing.AssignedUserId, existing.AssignedUser.Username));
                continue;
            }
            // A microsecond apart (what Postgres keeps), so lists keep the order they were assigned in (by recipe, then
            // clay body) instead of id order.
            var at = now.AddMicroseconds(created.Count);
            var task = new TileTask
            {
                Id = Guid.CreateVersion7(), RecipeId = recipe.Id, ClayBody = clayBody, AssignedUserId = participant.Id, AssignedAt = at,
                DueOn = dueOn, Note = note, CreatedBy = adminId, CreatedAt = at, UpdatedAt = at
            };
            task.Events.Add(Event(task, TileTaskEventKind.Assigned, adminId, at, toUserId: participant.Id));
            created.Add(task);
        }
        db.TileTasks.AddRange(created);
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" })
        {
            throw ApiProblems.Conflict("task_exists", "Some of these tiles were assigned by someone else just now. Refresh and assign again.");
        }
        logger.LogInformation("Admin {AdminId} assigned {Created} tile tasks to {UserId} ({Skipped} already assigned)", adminId, created.Count, participant.Id, skipped.Count);
        var createdIds = created.Select(c => c.Id).ToList();
        var rows = await Rows(db.TileTasks.AsNoTracking().Where(t => createdIds.Contains(t.Id))).ToListAsync(cancellationToken);
        var dtos = await MapAsync(Ordered(rows.AsQueryable()).ToList(), false, cancellationToken);
        return new AssignTileTasksResponse(dtos, skipped, await CanUploadAsync(participant.Id, cancellationToken));
    }

    public async Task<ReassignTileTasksResponse> ReassignAsync(IReadOnlyCollection<Guid> taskIds, Guid toUserId, DateOnly? dueOn, string? reason, Guid? version, bool single,
        Guid adminId, CancellationToken cancellationToken)
    {
        var ids = taskIds.Distinct().ToList();
        if (ids.Count == 0) throw ApiProblems.Validation("taskIds", "Choose at least one task.");
        if (ids.Count > MaxTasksPerReassign) throw ApiProblems.Validation("taskIds", $"Reassign at most {MaxTasksPerReassign} tasks at a time.");
        var due = DueOn(dueOn, "dueOn");
        var note = Note(reason, "reason");
        var participant = await ParticipantAsync(toUserId, cancellationToken);

        var rows = await Rows(db.TileTasks.Where(t => ids.Contains(t.Id))).ToListAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var moved = new List<Row>();
        var skipped = new List<SkippedTaskDto>();
        foreach (var id in ids)
        {
            var row = rows.FirstOrDefault(r => r.Task.Id == id);
            var why = row is null ? "not_found" : WhyNotMovable(row) ?? (row.Task.AssignedUserId == participant.Id ? "same_participant" : null);
            if (single && why is not null) throw Refusal(why, participant);
            if (single && version is { } v && v != row!.Task.ConcurrencyStamp) throw Changed();
            if (why is not null) { skipped.Add(new SkippedTaskDto(id, why)); continue; }

            var task = row!.Task;
            db.TileTaskEvents.Add(Event(task, TileTaskEventKind.Reassigned, adminId, now, fromUserId: task.AssignedUserId, toUserId: participant.Id, note: note));
            task.AssignedUserId = participant.Id;
            task.AssignedAt = now;
            // The new participant starts afresh: presses Start, makes it, says when it is made, and it is checked again.
            task.StartedAt = null;
            task.DoneAt = null;
            task.ApprovedAt = null;
            task.ApprovedBy = null;
            task.ReturnNote = null;
            if (due is not null) task.DueOn = due;
            Touch(task, now);
            moved.Add(row);
        }
        await SaveAsync(cancellationToken);
        logger.LogInformation("Admin {AdminId} reassigned {Moved} tile tasks to {UserId} ({Skipped} skipped)", adminId, moved.Count, participant.Id, skipped.Count);
        var movedIds = moved.Select(m => m.Task.Id).ToList();
        var fresh = await Rows(db.TileTasks.AsNoTracking().Where(t => movedIds.Contains(t.Id))).ToListAsync(cancellationToken);
        return new ReassignTileTasksResponse(await MapAsync(Ordered(fresh.AsQueryable()).ToList(), false, cancellationToken), skipped,
            await CanUploadAsync(participant.Id, cancellationToken));
    }

    public async Task<TileTaskDto> UpdateAsync(Guid taskId, UpdateTileTaskRequest request, Guid adminId, CancellationToken cancellationToken)
    {
        var row = await Rows(db.TileTasks.Where(t => t.Id == taskId)).SingleOrDefaultAsync(cancellationToken) ?? throw TaskNotFound();
        var task = row.Task;
        if (task.CancelledAt is not null) throw ApiProblems.Conflict("task_cancelled", "This task was cancelled.");
        if (request.Version is { } v && v != task.ConcurrencyStamp) throw Changed();
        var now = DateTimeOffset.UtcNow;
        var changes = new List<string>();
        if (request.ClearDueOn == true && task.DueOn is not null) { task.DueOn = null; changes.Add("due date removed"); }
        else if (request.DueOn is { } due && due != task.DueOn) { task.DueOn = DueOn(due, "dueOn"); changes.Add($"due {due:yyyy-MM-dd}"); }
        if (request.Note is not null)
        {
            var note = Note(request.Note, "note");
            if (note != task.Note) { task.Note = note; changes.Add(note is null ? "note removed" : "note changed"); }
        }
        if (changes.Count > 0)
        {
            db.TileTaskEvents.Add(Event(task, TileTaskEventKind.Updated, adminId, now, note: string.Join(", ", changes)));
            Touch(task, now);
            await SaveAsync(cancellationToken);
        }
        return await GetAsync(taskId, null, true, cancellationToken);
    }

    public async Task<TileTaskDto> CancelAsync(Guid taskId, CancelTileTaskRequest request, Guid adminId, CancellationToken cancellationToken)
    {
        var row = await Rows(db.TileTasks.Where(t => t.Id == taskId)).SingleOrDefaultAsync(cancellationToken) ?? throw TaskNotFound();
        if (row.Task.CancelledAt is not null) return await GetAsync(taskId, null, true, cancellationToken); // cancelling twice changes nothing
        if (WhyLocked(row) is { } why) throw Refusal(why, null);
        if (request.Version is { } v && v != row.Task.ConcurrencyStamp) throw Changed();
        var now = DateTimeOffset.UtcNow;
        row.Task.CancelledAt = now;
        db.TileTaskEvents.Add(Event(row.Task, TileTaskEventKind.Cancelled, adminId, now, note: Note(request.Reason, "reason")));
        Touch(row.Task, now);
        await SaveAsync(cancellationToken);
        logger.LogInformation("Admin {AdminId} cancelled tile task {TaskId}", adminId, taskId);
        return await GetAsync(taskId, null, true, cancellationToken);
    }

    /// <summary>
    /// Brings a cancelled (deleted) task back to the participant it was with, at the stage it had reached. Refused while
    /// its recipe is deleted (restore the recipe instead) or once the same tile has been given out again.
    /// </summary>
    public async Task<TileTaskDto> RestoreAsync(Guid taskId, Guid adminId, CancellationToken cancellationToken)
    {
        var task = await db.TileTasks.Include(t => t.Recipe).SingleOrDefaultAsync(t => t.Id == taskId, cancellationToken) ?? throw TaskNotFound();
        if (task.CancelledAt is null) return await GetAsync(taskId, null, true, cancellationToken); // restoring twice changes nothing
        if (task.Recipe.DeletedAt is not null)
            throw ApiProblems.Conflict("recipe_deleted", $"{task.Recipe.Code ?? task.Recipe.Name} is deleted. Restore the recipe; its tiles come back with it.");
        if (await db.TileTasks.AnyAsync(t => t.RecipeId == task.RecipeId && t.ClayBody == task.ClayBody && t.CancelledAt == null && t.Id != task.Id, cancellationToken))
            throw ApiProblems.Conflict("task_exists", "This tile has been given out again since, so this one stays deleted.");
        var now = DateTimeOffset.UtcNow;
        task.CancelledAt = null;
        db.TileTaskEvents.Add(Event(task, TileTaskEventKind.Restored, adminId, now));
        Touch(task, now);
        await SaveAsync(cancellationToken);
        logger.LogInformation("Admin {AdminId} restored tile task {TaskId}", adminId, taskId);
        return await GetAsync(taskId, null, true, cancellationToken);
    }

    /// <summary>
    /// Everything that happened to one participant's work, newest first: their tasks' history (by them or an admin,
    /// including tasks that went from or to them) and the photos they sent. At most `limit` items.
    /// </summary>
    public async Task<IReadOnlyList<ParticipantActivityDto>> ActivityAsync(Guid userId, int? limit, CancellationToken cancellationToken)
    {
        var take = Math.Clamp(limit ?? 100, 1, 500);
        var events = await db.TileTaskEvents.AsNoTracking()
            .Where(e => e.ActorUserId == userId || e.FromUserId == userId || e.ToUserId == userId || e.TileTask.AssignedUserId == userId)
            .OrderByDescending(e => e.At).Take(take)
            .Select(e => new { e.Kind, e.At, e.TileTaskId, e.TileTask.RecipeId, e.TileTask.ClayBody, e.ActorUserId, e.Note })
            .ToListAsync(cancellationToken);
        var uploads = await db.Uploads.AsNoTracking().Where(u => u.OwnerUserId == userId && Sent.Contains(u.Status))
            .OrderByDescending(u => u.CreatedAt).Take(take)
            .Select(u => new { u.Id, u.CreatedAt, u.TileTaskId, u.RecipeId, u.ClayBody, u.Status, u.ReviewNote })
            .ToListAsync(cancellationToken);
        var recipeIds = events.Select(e => (Guid?)e.RecipeId).Concat(uploads.Select(u => u.RecipeId)).OfType<Guid>().Distinct().ToList();
        var recipes = await db.Recipes.AsNoTracking().Where(r => recipeIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, r => new { r.Code, r.Name }, cancellationToken);
        var actorIds = events.Select(e => e.ActorUserId).Append(userId).Distinct().ToList();
        var names = await db.Users.AsNoTracking().Where(u => actorIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.Username, cancellationToken);
        return events.Select(e => new ParticipantActivityDto(e.Kind.ToString(), e.At, e.TileTaskId, e.RecipeId, recipes.GetValueOrDefault(e.RecipeId)?.Code,
                recipes.GetValueOrDefault(e.RecipeId)?.Name, e.ClayBody, names.GetValueOrDefault(e.ActorUserId), e.Note))
            .Concat(uploads.Select(u => new ParticipantActivityDto("PhotoSent", u.CreatedAt, u.TileTaskId, u.RecipeId,
                u.RecipeId is { } r ? recipes.GetValueOrDefault(r)?.Code : null, u.RecipeId is { } n ? recipes.GetValueOrDefault(n)?.Name : null, u.ClayBody,
                names.GetValueOrDefault(userId), u.ReviewNote, u.Id, u.Status)))
            .OrderByDescending(a => a.At).Take(take).ToList();
    }

    // ---- admin: checking made tiles ----

    /// <summary>Approves made tiles (stage Done) for their photo: each can then be fired and photographed. The rest are skipped, with why.</summary>
    public async Task<TileTaskBatchResponse> ApproveAsync(IReadOnlyCollection<Guid>? taskIds, Guid adminId, CancellationToken cancellationToken)
    {
        var (rows, skipped) = await PickAsync(taskIds, (_, stage) => stage == TileTaskStage.Done ? null : Reason(stage), cancellationToken);
        var now = DateTimeOffset.UtcNow;
        foreach (var row in rows)
        {
            row.Task.ApprovedAt = now;
            row.Task.ApprovedBy = adminId;
            db.TileTaskEvents.Add(Event(row.Task, TileTaskEventKind.CompositionApproved, adminId, now));
            Touch(row.Task, now);
        }
        await SaveAsync(cancellationToken);
        logger.LogInformation("Admin {AdminId} approved {Count} made tiles ({Skipped} skipped)", adminId, rows.Count, skipped.Count);
        return new TileTaskBatchResponse(await FreshAsync(rows, cancellationToken), skipped);
    }

    /// <summary>
    /// Sends made tiles back to their participant to make again, with the reason they see. Also undoes an approval while
    /// no photo has been sent. A reason is required. The recipe stays locked: they have started it.
    /// </summary>
    public async Task<TileTaskBatchResponse> ReturnAsync(IReadOnlyCollection<Guid>? taskIds, string? reason, Guid adminId, CancellationToken cancellationToken)
    {
        var note = Note(reason, "reason") ?? throw ApiProblems.Validation("reason", "Say what needs to change, so the participant knows.");
        var (rows, skipped) = await PickAsync(taskIds, (row, stage) =>
            stage == TileTaskStage.Done || (stage == TileTaskStage.AwaitingPhoto && row.Photo is null) ? null : Reason(stage), cancellationToken);
        var now = DateTimeOffset.UtcNow;
        foreach (var row in rows)
        {
            row.Task.DoneAt = null;
            row.Task.ApprovedAt = null;
            row.Task.ApprovedBy = null;
            row.Task.StartedAt ??= now;
            row.Task.ReturnNote = note;
            db.TileTaskEvents.Add(Event(row.Task, TileTaskEventKind.Returned, adminId, now, note: note));
            Touch(row.Task, now);
        }
        await SaveAsync(cancellationToken);
        logger.LogInformation("Admin {AdminId} returned {Count} made tiles ({Skipped} skipped)", adminId, rows.Count, skipped.Count);
        return new TileTaskBatchResponse(await FreshAsync(rows, cancellationToken), skipped);
    }

    private async Task<(List<Row> Rows, List<SkippedTaskDto> Skipped)> PickAsync(IReadOnlyCollection<Guid>? taskIds, Func<Row, TileTaskStage, string?> whyNot,
        CancellationToken cancellationToken)
    {
        var ids = (taskIds ?? []).Distinct().ToList();
        if (ids.Count == 0) throw ApiProblems.Validation("taskIds", "Choose at least one task.");
        if (ids.Count > MaxTasksPerReassign) throw ApiProblems.Validation("taskIds", $"Choose at most {MaxTasksPerReassign} tasks at a time.");
        var found = await Rows(db.TileTasks.Where(t => ids.Contains(t.Id))).ToListAsync(cancellationToken);
        var picked = new List<Row>();
        var skipped = new List<SkippedTaskDto>();
        foreach (var id in ids)
        {
            var row = found.FirstOrDefault(r => r.Task.Id == id);
            var why = row is null ? "not_found" : whyNot(row, StageOf(row.Task, row.Photo));
            if (why is null) picked.Add(row!);
            else skipped.Add(new SkippedTaskDto(id, why));
        }
        return (picked, skipped);
    }

    private async Task<IReadOnlyList<TileTaskDto>> FreshAsync(List<Row> rows, CancellationToken cancellationToken)
    {
        var ids = rows.Select(r => r.Task.Id).ToList();
        var fresh = await Rows(db.TileTasks.AsNoTracking().Where(t => ids.Contains(t.Id))).ToListAsync(cancellationToken);
        return await MapAsync(Ordered(fresh.AsQueryable()).ToList(), false, cancellationToken);
    }

    /// <summary>A stage as a skipped-task reason: not_started, not_done, done, awaiting_photo, in_review, approved, cancelled.</summary>
    private static string Reason(TileTaskStage stage) => stage switch
    {
        TileTaskStage.Assigned => "not_started",
        TileTaskStage.Started => "not_done",
        TileTaskStage.Done => "done",
        TileTaskStage.AwaitingPhoto => "awaiting_photo",
        TileTaskStage.InReview => "in_review",
        TileTaskStage.Approved => "approved",
        _ => "cancelled"
    };

    // ---- participant ----

    /// <summary>Shows the participant the recipe and locks it, so nobody changes it while they mix. Saying it twice changes nothing.</summary>
    public async Task<TileTaskDto> StartAsync(Guid userId, Guid taskId, CancellationToken cancellationToken)
    {
        var row = await MineAsync(userId, taskId, cancellationToken);
        if (row.Task.StartedAt is null)
        {
            var now = DateTimeOffset.UtcNow;
            row.Task.StartedAt = now;
            db.TileTaskEvents.Add(Event(row.Task, TileTaskEventKind.Started, userId, now));
            Touch(row.Task, now);
            await SaveAsync(cancellationToken);
        }
        return await GetAsync(taskId, userId, false, cancellationToken);
    }

    /// <summary>The tile is made: it goes to an admin to check. Only after Start. Saying it twice changes nothing.</summary>
    public async Task<TileTaskDto> MarkDoneAsync(Guid userId, Guid taskId, CancellationToken cancellationToken)
    {
        var row = await MineAsync(userId, taskId, cancellationToken);
        if (row.Task.StartedAt is null && row.Task.DoneAt is null)
            throw ApiProblems.Conflict("task_not_started", "Press Start and make the tile before marking it done.");
        if (row.Task.DoneAt is null && row.Task.ApprovedAt is null && row.Photo is null)
        {
            var now = DateTimeOffset.UtcNow;
            row.Task.DoneAt = now;
            row.Task.ReturnNote = null;
            db.TileTaskEvents.Add(Event(row.Task, TileTaskEventKind.MarkedDone, userId, now));
            Touch(row.Task, now);
            await SaveAsync(cancellationToken);
        }
        return await GetAsync(taskId, userId, false, cancellationToken);
    }

    /// <summary>"I tapped Done by mistake": only until an admin has checked it.</summary>
    public async Task<TileTaskDto> UndoDoneAsync(Guid userId, Guid taskId, CancellationToken cancellationToken)
    {
        var row = await MineAsync(userId, taskId, cancellationToken);
        if (row.Photo is not null) throw ApiProblems.Conflict("task_photo_sent", "A photo of this tile has already been sent, so it stays done.");
        if (row.Task.ApprovedAt is not null) throw ApiProblems.Conflict("task_approved_for_photo", "Your admin has already checked this tile, so it stays done.");
        if (row.Task.DoneAt is not null)
        {
            var now = DateTimeOffset.UtcNow;
            row.Task.DoneAt = null;
            db.TileTaskEvents.Add(Event(row.Task, TileTaskEventKind.DoneUndone, userId, now));
            Touch(row.Task, now);
            await SaveAsync(cancellationToken);
        }
        return await GetAsync(taskId, userId, false, cancellationToken);
    }

    /// <summary>
    /// The task a new photo belongs to. With an id: it must be live, checked and approved by an admin (stage
    /// AwaitingPhoto, which includes a retake after a rejected photo); its recipe and clay body are the photo's. Anyone,
    /// participant or admin, may then photograph it by its code. Without an id: the caller's task for the same recipe
    /// and clay body, so photos taken the old way (picking the recipe) still count, under the same rule.
    /// </summary>
    public async Task<TileTask?> ForUploadAsync(Guid userId, Guid? taskId, Recipe? recipe, string? clayBody, DateTimeOffset now, CancellationToken cancellationToken)
    {
        Row? row;
        if (taskId is { } id)
        {
            row = await Rows(db.TileTasks.Where(t => t.Id == id && t.CancelledAt == null)).SingleOrDefaultAsync(cancellationToken)
                ?? throw new ApiProblemException(400, "task_not_found", "Task not found", "This tile task does not exist, or it was cancelled.");
            if (WhyNotPhotographable(row) is { } why) throw Refusal(why, null);
            // The photo is of the task's recipe, loaded here so the upload links to it.
            row.Task.Recipe = await db.Recipes.Include(r => r.BaseGlaze).SingleAsync(r => r.Id == row.Task.RecipeId, cancellationToken);
            if (recipe is not null && recipe.Id != row.Task.RecipeId)
                throw new ApiProblemException(400, "task_recipe_mismatch", "Wrong recipe", $"This task is for {row.Task.Recipe.Code ?? row.Task.Recipe.Name}, not {recipe.Code ?? recipe.Name}.");
            if (clayBody is not null && clayBody != row.Task.ClayBody)
                throw new ApiProblemException(400, "task_clay_body_mismatch", "Wrong clay body", $"This task is for a {row.Task.ClayBody.Replace('_', ' ')} tile.");
        }
        else
        {
            if (recipe is null || clayBody is null) return null;
            row = await Rows(db.TileTasks.Where(t => t.RecipeId == recipe.Id && t.ClayBody == clayBody && t.AssignedUserId == userId && t.CancelledAt == null))
                .SingleOrDefaultAsync(cancellationToken);
            if (row is null) return null;
            // Not checked yet: the photo would skip the admin's check. In review or approved: a further photo, not the task's.
            var why = WhyNotPhotographable(row);
            if (why is "not_started" or "not_done" or "not_approved") throw Refusal(why, null);
            if (why is not null) return null;
        }
        // Touched on every photo, so two people photographing the same tile at once can't both send one.
        Touch(row.Task, now);
        return row.Task;
    }

    /// <summary>
    /// The tiles made of one recipe, for capture by code: each live task with its clay body, maker and whether it can be
    /// photographed now (checked and approved by an admin). A rejected photo makes it ready again.
    /// </summary>
    public async Task<IReadOnlyList<CaptureTileDto>> TilesForCaptureAsync(Guid userId, Guid recipeId, CancellationToken cancellationToken)
    {
        var rows = await Rows(db.TileTasks.Where(t => t.RecipeId == recipeId && t.CancelledAt == null))
            .OrderBy(r => r.Task.ClayBody).ThenBy(r => r.Task.AssignedAt).ToListAsync(cancellationToken);
        return (await MapAsync(rows, false, cancellationToken)).Select(t =>
        {
            var status = t.Stage switch
            {
                TileTaskStage.AwaitingPhoto => "ready",
                TileTaskStage.Assigned or TileTaskStage.Started => "not_done",
                TileTaskStage.Done => "not_approved",
                TileTaskStage.InReview => "in_review",
                _ => "approved"
            };
            return new CaptureTileDto(t.Id, t.ClayBody, t.Stage, status, t.AssignedUsername, t.AssignedUserId == userId, t.DoneAt, t.RetakeReason);
        }).ToList();
    }

    /// <summary>A live task for this recipe with the caller lets them photograph it, as a capture-code assignment does.</summary>
    public Task<bool> HasLiveTaskAsync(Guid userId, Guid recipeId, CancellationToken cancellationToken) =>
        db.TileTasks.AnyAsync(t => t.AssignedUserId == userId && t.RecipeId == recipeId && t.CancelledAt == null, cancellationToken);

    // ---- helpers ----

    private async Task<Row> MineAsync(Guid userId, Guid taskId, CancellationToken cancellationToken) =>
        await Rows(db.TileTasks.Where(t => t.Id == taskId && t.AssignedUserId == userId && t.CancelledAt == null)).SingleOrDefaultAsync(cancellationToken)
        ?? throw TaskNotFound();

    /// <summary>Why a task can no longer be cancelled; null until a photo of it is in review or approved.</summary>
    private static string? WhyLocked(Row row) => StageOf(row.Task, row.Photo) switch
    {
        TileTaskStage.Cancelled => "cancelled",
        TileTaskStage.InReview => "in_review",
        TileTaskStage.Approved => "approved",
        _ => null
    };

    /// <summary>A tile moves to someone else until an admin has checked it (meeting 2026-11-02); the new participant starts afresh.</summary>
    private static string? WhyNotMovable(Row row) => StageOf(row.Task, row.Photo) switch
    {
        TileTaskStage.Assigned or TileTaskStage.Started or TileTaskStage.Done => null,
        TileTaskStage.AwaitingPhoto => "awaiting_photo",
        _ => WhyLocked(row)
    };

    /// <summary>A photo only once an admin has checked the made tile and approved it.</summary>
    private static string? WhyNotPhotographable(Row row) => StageOf(row.Task, row.Photo) switch
    {
        TileTaskStage.AwaitingPhoto => null,
        TileTaskStage.Assigned => "not_started",
        TileTaskStage.Started => "not_done",
        TileTaskStage.Done => "not_approved",
        _ => WhyLocked(row)
    };

    private static ApiProblemException Refusal(string why, AppUser? participant) => why switch
    {
        "not_found" => TaskNotFound(),
        "cancelled" => ApiProblems.Conflict("task_cancelled", "This task was cancelled."),
        "in_review" => ApiProblems.Conflict("task_in_review", "A photo of this tile is waiting for review. Approve or reject it first."),
        "approved" => ApiProblems.Conflict("task_approved", "This tile's photo is already approved."),
        "not_started" or "not_done" => ApiProblems.Conflict("task_not_done", "This tile isn't made yet. It can be photographed once it is made and an admin has checked it."),
        "not_approved" => ApiProblems.Conflict("task_not_approved", "This tile is waiting for an admin to check it. It can be photographed once it is approved."),
        "done" => ApiProblems.Conflict("task_done", "This tile is already made. Return it first if it should be made again by someone else."),
        "awaiting_photo" => ApiProblems.Conflict("task_awaiting_photo", "This tile is made and approved, waiting for its photo, so it stays with whoever made it."),
        _ => ApiProblems.Validation("assignedUserId", $"This task is already with {participant?.Username ?? "that participant"}.")
    };

    private static ApiProblemException TaskNotFound() => new(404, "task_not_found", "Task not found", "No task with this ID was found.");

    private static ApiProblemException Changed() =>
        ApiProblems.Conflict("task_changed", "Someone changed this task since you opened it. Refresh and try again.");

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException) { throw Changed(); }
    }

    private static void Touch(TileTask task, DateTimeOffset now)
    {
        task.UpdatedAt = now;
        task.ConcurrencyStamp = Guid.NewGuid();
    }

    internal static TileTaskEvent Event(TileTask task, TileTaskEventKind kind, Guid actorId, DateTimeOffset at, Guid? fromUserId = null, Guid? toUserId = null, string? note = null) =>
        new() { Id = Guid.CreateVersion7(), TileTaskId = task.Id, Kind = kind, ActorUserId = actorId, FromUserId = fromUserId, ToUserId = toUserId, Note = note, At = at };

    /// <summary>Only active participants (not admins, not pending or suspended accounts) take tasks.</summary>
    private async Task<AppUser> ParticipantAsync(Guid userId, CancellationToken cancellationToken)
    {
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == userId, cancellationToken)
                   ?? throw ApiProblems.Validation("assignedUserId", "That participant does not exist.");
        if (user.Role != UserRole.User) throw ApiProblems.Validation("assignedUserId", "Tasks go to participants, not administrators.");
        if (user.Status != AccountStatus.Active) throw ApiProblems.Validation("assignedUserId", $"@{user.Username}'s account is not active.");
        return user;
    }

    private Task<bool> CanUploadAsync(Guid userId, CancellationToken cancellationToken) =>
        db.CaptureCodes.Where(x => x.AssignedUserId == userId).Usable(DateTimeOffset.UtcNow).AnyAsync(cancellationToken);

    private List<string> ClayBodies(IReadOnlyCollection<string>? requested)
    {
        var options = ingestion.Value;
        if (requested is null || requested.Count == 0) return [.. options.AllowedClayBodies];
        var result = new List<string>();
        foreach (var value in requested)
        {
            if (!options.TryNormalizeClayBody(value, out var clayBody) || clayBody is null)
                throw ApiProblems.Validation("clayBodies", $"Clay bodies must be among {string.Join(", ", options.AllowedClayBodies)}.");
            if (!result.Contains(clayBody)) result.Add(clayBody);
        }
        return result;
    }

    /// <summary>A due date can be today or later; one already past is almost always a typo.</summary>
    private static DateOnly? DueOn(DateOnly? value, string field) =>
        value is { } due && due < Today ? throw ApiProblems.Validation(field, "The due date has already passed.") : value;

    private static string? Note(string? value, string field)
    {
        var note = value?.Trim();
        if (string.IsNullOrEmpty(note)) return null;
        return note.Length > MaxNoteLength ? throw ApiProblems.Validation(field, $"Keep it to {MaxNoteLength} characters.") : note;
    }
}
