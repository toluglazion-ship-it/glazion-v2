using Glazion.Api.Contracts;
using Glazion.Api.Data;
using Glazion.Api.Domain;
using Glazion.Api.Chemistry;
using Glazion.Api.Infrastructure;
using Glazion.Api.Retrieval;
using Glazion.Api.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Glazion.Api.Tests;

public sealed class TileTaskTests
{
    private sealed class Fixture : IAsyncDisposable
    {
        public GlazionDbContext Db { get; } = new(new DbContextOptionsBuilder<GlazionDbContext>().UseInMemoryDatabase("tasks-" + Guid.NewGuid()).Options);
        public AppUser Admin { get; } = new() { Id = Guid.NewGuid(), Username = "admin", Role = UserRole.Admin, Status = AccountStatus.Active };
        public AppUser Ada { get; } = new() { Id = Guid.NewGuid(), Username = "ada", Role = UserRole.User, Status = AccountStatus.Active, ParticipantCode = "A01" };
        public AppUser Ben { get; } = new() { Id = Guid.NewGuid(), Username = "ben", Role = UserRole.User, Status = AccountStatus.Active, ParticipantCode = "A02" };
        public AppUser Pending { get; } = new() { Id = Guid.NewGuid(), Username = "new", Role = UserRole.User, Status = AccountStatus.Pending };
        public Recipe Celadon { get; } = new() { Id = Guid.NewGuid(), Name = "Celadon", Code = "A01-B1-001" };
        public Recipe Shino { get; } = new() { Id = Guid.NewGuid(), Name = "Shino" };
        public TileTaskService Tasks { get; }

        public Fixture() => Tasks = new TileTaskService(Db, Options.Create(new IngestionOptions()), NullLogger<TileTaskService>.Instance);

        public async Task<Fixture> InitAsync()
        {
            Db.Users.AddRange(Admin, Ada, Ben, Pending);
            Db.Recipes.AddRange(Celadon, Shino);
            await Db.SaveChangesAsync();
            return this;
        }

        public Task<AssignTileTasksResponse> AssignAsync(AppUser to, IReadOnlyCollection<Guid> recipes, IReadOnlyCollection<string>? bodies = null, DateOnly? dueOn = null) =>
            Tasks.AssignAsync(new AssignTileTasksRequest(recipes, bodies, to.Id, dueOn, "Two coats, dipped"), Admin.Id, CancellationToken.None);

        /// <summary>A photo sent for the task by its owner, at a given status (what the capture flow and review leave behind).</summary>
        public async Task<Upload> PhotoAsync(Guid taskId, AppUser owner, UploadStatus status, string? reviewNote = null)
        {
            var task = await Db.TileTasks.AsNoTracking().SingleAsync(t => t.Id == taskId);
            var upload = new Upload
            {
                Id = Guid.CreateVersion7(), OwnerUserId = owner.Id, CaptureCodeId = Guid.NewGuid(), RecipeId = task.RecipeId, ClayBody = task.ClayBody, TileTaskId = taskId,
                ObjectKey = $"k/{Guid.NewGuid()}", OriginalFileName = "tile.jpg", ContentType = "image/jpeg", Status = status, ReviewNote = reviewNote,
                CreatedAt = DateTimeOffset.UtcNow
            };
            Db.Uploads.Add(upload);
            await Db.SaveChangesAsync();
            Db.ChangeTracker.Clear();
            return upload;
        }

        public Task<TileTaskDto> GetAsync(Guid taskId) => Tasks.GetAsync(taskId, null, true, CancellationToken.None);

        /// <summary>Its participant starts it and marks it made.</summary>
        public async Task<TileTaskDto> MadeAsync(Guid taskId, AppUser by)
        {
            await Tasks.StartAsync(by.Id, taskId, CancellationToken.None);
            return await Tasks.MarkDoneAsync(by.Id, taskId, CancellationToken.None);
        }

        /// <summary>Made, then checked and approved by the admin: ready to be photographed.</summary>
        public async Task<TileTaskDto> ApprovedAsync(Guid taskId, AppUser by)
        {
            await MadeAsync(taskId, by);
            Db.ChangeTracker.Clear();
            return Assert.Single((await Tasks.ApproveAsync([taskId], Admin.Id, CancellationToken.None)).Updated);
        }

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    [Fact]
    public async Task Assigning_makes_one_task_per_clay_body_and_assigning_again_creates_nothing()
    {
        await using var f = await new Fixture().InitAsync();

        var first = await f.AssignAsync(f.Ada, [f.Celadon.Id, f.Shino.Id], dueOn: TileTaskService.Today.AddDays(3));
        var again = await f.AssignAsync(f.Ben, [f.Celadon.Id], ["porcelain"]);

        Assert.Equal(6, first.Created.Count); // 2 recipes x 3 clay bodies, in the order assigned
        Assert.Equal(["porcelain", "stoneware", "red_body"], first.Created.Where(t => t.RecipeId == f.Celadon.Id).Select(t => t.ClayBody));
        Assert.All(first.Created, t => Assert.Equal((TileTaskStage.Assigned, "ada", "Two coats, dipped"), (t.Stage, t.AssignedUsername, t.Note)));
        Assert.False(first.ParticipantCanUpload); // no capture code yet
        Assert.Empty(again.Created);
        var skipped = Assert.Single(again.Skipped);
        Assert.Equal(("porcelain", "ada"), (skipped.ClayBody, skipped.AssignedUsername));
        var history = Assert.Single((await f.GetAsync(first.Created[0].Id)).History!);
        Assert.Equal((TileTaskEventKind.Assigned, "admin", "ada"), (history.Kind, history.ActorUsername, history.ToUsername));
    }

    [Fact]
    public async Task Only_active_participants_take_tasks_and_due_dates_cannot_be_in_the_past()
    {
        await using var f = await new Fixture().InitAsync();

        var admin = await Assert.ThrowsAsync<ApiProblemException>(() => f.AssignAsync(f.Admin, [f.Celadon.Id]));
        var pending = await Assert.ThrowsAsync<ApiProblemException>(() => f.AssignAsync(f.Pending, [f.Celadon.Id]));
        var past = await Assert.ThrowsAsync<ApiProblemException>(() => f.AssignAsync(f.Ada, [f.Celadon.Id], dueOn: TileTaskService.Today.AddDays(-1)));
        var body = await Assert.ThrowsAsync<ApiProblemException>(() => f.AssignAsync(f.Ada, [f.Celadon.Id], ["earthenware"]));

        Assert.All(new[] { admin, pending, past, body }, ex => Assert.Equal(400, ex.Status));
        Assert.Empty(f.Db.TileTasks);
    }

    [Fact]
    public async Task A_task_goes_from_started_to_made_to_checked_to_photographed_and_back_to_its_photo_when_rejected()
    {
        await using var f = await new Fixture().InitAsync();
        var task = (await f.AssignAsync(f.Ada, [f.Celadon.Id], ["stoneware"])).Created[0];

        var notStarted = await Assert.ThrowsAsync<ApiProblemException>(() => f.Tasks.MarkDoneAsync(f.Ada.Id, task.Id, CancellationToken.None));
        var started = await f.Tasks.StartAsync(f.Ada.Id, task.Id, CancellationToken.None);
        var done = await f.Tasks.MarkDoneAsync(f.Ada.Id, task.Id, CancellationToken.None);
        f.Db.ChangeTracker.Clear();
        var approved = Assert.Single((await f.Tasks.ApproveAsync([task.Id], f.Admin.Id, CancellationToken.None)).Updated);
        var rejected = await f.PhotoAsync(task.Id, f.Ada, UploadStatus.NeedsReview);
        var inReview = await f.GetAsync(task.Id);
        rejected.Status = UploadStatus.Rejected; rejected.ReviewNote = "Out of focus";
        f.Db.Uploads.Update(rejected); await f.Db.SaveChangesAsync(); f.Db.ChangeTracker.Clear();
        var retake = await f.GetAsync(task.Id);
        await f.PhotoAsync(task.Id, f.Ada, UploadStatus.Indexed);
        var final = await f.GetAsync(task.Id);

        Assert.Equal((409, "task_not_started"), (notStarted.Status, notStarted.Code));
        Assert.Equal((TileTaskStage.Started, TileTaskStage.Done, TileTaskStage.AwaitingPhoto), (started.Stage, done.Stage, approved.Stage));
        Assert.NotNull(approved.ApprovedAt);
        Assert.Equal(TileTaskStage.InReview, inReview.Stage);
        Assert.Equal((TileTaskStage.AwaitingPhoto, "Out of focus"), (retake.Stage, retake.RetakeReason));
        Assert.Equal((TileTaskStage.Approved, (string?)null), (final.Stage, final.RetakeReason));
        Assert.Equal([TileTaskEventKind.Assigned, TileTaskEventKind.Started, TileTaskEventKind.MarkedDone, TileTaskEventKind.CompositionApproved], final.History!.Select(e => e.Kind));
        Assert.Equal(new TileTaskSummaryDto(0, 0, 0, 1, 0, 0, 0, 0), await f.Tasks.SummaryAsync(f.Db.TileTasks, CancellationToken.None));
    }

    [Fact]
    public async Task An_admin_approves_made_tiles_or_returns_them_with_a_reason_and_the_rest_are_skipped()
    {
        await using var f = await new Fixture().InitAsync();
        var created = (await f.AssignAsync(f.Ada, [f.Celadon.Id], ["porcelain", "stoneware", "red_body"])).Created;
        await f.MadeAsync(created[0].Id, f.Ada);
        await f.MadeAsync(created[1].Id, f.Ada);
        f.Db.ChangeTracker.Clear();

        var approve = await f.Tasks.ApproveAsync([created[0].Id, created[2].Id], f.Admin.Id, CancellationToken.None);
        var noReason = await Assert.ThrowsAsync<ApiProblemException>(() => f.Tasks.ReturnAsync([created[1].Id], " ", f.Admin.Id, CancellationToken.None));
        f.Db.ChangeTracker.Clear();
        var back = await f.Tasks.ReturnAsync([created[1].Id], "Glaze too thin, dip twice", f.Admin.Id, CancellationToken.None);
        var forAda = await f.Tasks.GetAsync(created[1].Id, f.Ada.Id, false, CancellationToken.None);
        var summary = await f.Tasks.SummaryAsync(f.Db.TileTasks, CancellationToken.None);
        var again = await f.Tasks.MarkDoneAsync(f.Ada.Id, created[1].Id, CancellationToken.None);

        Assert.Equal(created[0].Id, Assert.Single(approve.Updated).Id);
        Assert.Equal([(created[2].Id, "not_started")], approve.Skipped.Select(x => (x.TaskId, x.Reason)));
        Assert.Equal(400, noReason.Status);
        Assert.Equal((TileTaskStage.Started, "Glaze too thin, dip twice"), (Assert.Single(back.Updated).Stage, forAda.ReturnNote));
        Assert.Equal((1, 0, 1, 1), (summary.Assigned, summary.Done, summary.Started, summary.AwaitingPhoto));
        Assert.Equal((TileTaskStage.Done, (string?)null), (again.Stage, again.ReturnNote)); // made again: back to the admin
    }

    [Fact]
    public async Task Reassigning_moves_an_open_task_to_a_fresh_start_and_keeps_its_history()
    {
        await using var f = await new Fixture().InitAsync();
        var created = (await f.AssignAsync(f.Ada, [f.Celadon.Id], ["porcelain", "stoneware"])).Created;
        var task = created[0];
        await f.Tasks.StartAsync(f.Ada.Id, task.Id, CancellationToken.None);
        await f.MadeAsync(created[1].Id, f.Ada);
        f.Db.ChangeTracker.Clear();

        var moved = (await f.Tasks.ReassignAsync([task.Id], f.Ben.Id, TileTaskService.Today.AddDays(2), "Ada did not come in", null, true, f.Admin.Id, CancellationToken.None)).Reassigned[0];
        // A made tile not yet checked moves too (meeting 2026-11-02): Ben makes it afresh.
        var made = (await f.Tasks.ReassignAsync([created[1].Id], f.Ben.Id, null, null, null, true, f.Admin.Id, CancellationToken.None)).Reassigned[0];
        var detail = await f.GetAsync(task.Id);

        // Ben starts from "to make": Ada's start belongs to her assignment
        Assert.Equal((TileTaskStage.Assigned, "ben", (DateTimeOffset?)null, (DateTimeOffset?)null), (moved.Stage, moved.AssignedUsername, moved.StartedAt, moved.DoneAt));
        Assert.Equal(TileTaskService.Today.AddDays(2), moved.DueOn);
        Assert.Equal((TileTaskStage.Assigned, (DateTimeOffset?)null), (made.Stage, made.DoneAt));
        Assert.Equal([TileTaskEventKind.Assigned, TileTaskEventKind.Started, TileTaskEventKind.Reassigned], detail.History!.Select(e => e.Kind));
        var reassigned = detail.History![2];
        Assert.Equal(("ada", "ben", "Ada did not come in"), (reassigned.FromUsername, reassigned.ToUsername, reassigned.Note));
        // Ada no longer sees it; Ben does
        await Assert.ThrowsAsync<ApiProblemException>(() => f.Tasks.StartAsync(f.Ada.Id, task.Id, CancellationToken.None));
        Assert.Equal(TileTaskStage.Done, (await f.MadeAsync(task.Id, f.Ben)).Stage);
    }

    [Fact]
    public async Task A_deleted_task_can_be_restored_where_it_was_and_a_started_one_hears_of_recipe_changes()
    {
        await using var f = await new Fixture().InitAsync();
        var task = (await f.AssignAsync(f.Ada, [f.Celadon.Id], ["porcelain"])).Created[0];
        await f.Tasks.StartAsync(f.Ada.Id, task.Id, CancellationToken.None);

        // The admin changes the recipe after Ada pressed Start: her task says so, until it is approved.
        var celadon = await f.Db.Recipes.SingleAsync(r => r.Id == f.Celadon.Id);
        celadon.MixChangedAt = DateTimeOffset.UtcNow.AddSeconds(1);
        await f.Db.SaveChangesAsync();
        Assert.Equal(celadon.MixChangedAt, (await f.GetAsync(task.Id)).RecipeChangedAt);

        await f.Tasks.CancelAsync(task.Id, new CancelTileTaskRequest(null, null), f.Admin.Id, CancellationToken.None);
        var restored = await f.Tasks.RestoreAsync(task.Id, f.Admin.Id, CancellationToken.None);
        Assert.Equal((TileTaskStage.Started, (DateTimeOffset?)null), (restored.Stage, restored.CancelledAt));
        Assert.Equal(TileTaskEventKind.Restored, restored.History!.Last().Kind);
        // Ada's admin page shows it all, newest first.
        Assert.Equal(["Restored", "Cancelled", "Started", "Assigned"], (await f.Tasks.ActivityAsync(f.Ada.Id, null, CancellationToken.None)).Select(a => a.Kind));

        // Given out again since: the deleted one stays deleted.
        await f.Tasks.CancelAsync(task.Id, new CancelTileTaskRequest(null, null), f.Admin.Id, CancellationToken.None);
        await f.AssignAsync(f.Ben, [f.Celadon.Id], ["porcelain"]);
        Assert.Equal("task_exists", (await Assert.ThrowsAsync<ApiProblemException>(() => f.Tasks.RestoreAsync(task.Id, f.Admin.Id, CancellationToken.None))).Code);
    }

    [Fact]
    public async Task A_task_whose_photo_is_in_review_cannot_be_moved_or_cancelled_and_bulk_moves_say_why()
    {
        await using var f = await new Fixture().InitAsync();
        var created = (await f.AssignAsync(f.Ada, [f.Celadon.Id], ["porcelain", "stoneware"])).Created;
        await f.PhotoAsync(created[0].Id, f.Ada, UploadStatus.NeedsReview);

        var single = await Assert.ThrowsAsync<ApiProblemException>(() => f.Tasks.ReassignAsync([created[0].Id], f.Ben.Id, null, null, null, true, f.Admin.Id, CancellationToken.None));
        var cancel = await Assert.ThrowsAsync<ApiProblemException>(() => f.Tasks.CancelAsync(created[0].Id, new CancelTileTaskRequest(null, null), f.Admin.Id, CancellationToken.None));
        var bulk = await f.Tasks.ReassignAsync([created[0].Id, created[1].Id, Guid.NewGuid()], f.Ben.Id, null, null, null, false, f.Admin.Id, CancellationToken.None);

        Assert.Equal((409, "task_in_review"), (single.Status, single.Code));
        Assert.Equal((409, "task_in_review"), (cancel.Status, cancel.Code));
        Assert.Equal(created[1].Id, Assert.Single(bulk.Reassigned).Id);
        Assert.Equal(["in_review", "not_found"], bulk.Skipped.Select(s => s.Reason));
    }

    [Fact]
    public async Task A_stale_version_is_refused_and_a_cancelled_tile_can_be_assigned_again()
    {
        await using var f = await new Fixture().InitAsync();
        var task = (await f.AssignAsync(f.Ada, [f.Shino.Id], ["red_body"])).Created[0];
        await f.Tasks.UpdateAsync(task.Id, new UpdateTileTaskRequest(TileTaskService.Today.AddDays(5), null, null, task.Version), f.Admin.Id, CancellationToken.None);

        var stale = await Assert.ThrowsAsync<ApiProblemException>(() => f.Tasks.CancelAsync(task.Id, new CancelTileTaskRequest(null, task.Version), f.Admin.Id, CancellationToken.None));
        var cancelled = await f.Tasks.CancelAsync(task.Id, new CancelTileTaskRequest("Recipe withdrawn", null), f.Admin.Id, CancellationToken.None);
        var reassigned = await f.AssignAsync(f.Ben, [f.Shino.Id], ["red_body"]);

        Assert.Equal((409, "task_changed"), (stale.Status, stale.Code));
        Assert.Equal(TileTaskStage.Cancelled, cancelled.Stage);
        Assert.Equal("ben", Assert.Single(reassigned.Created).AssignedUsername);
        await Assert.ThrowsAsync<ApiProblemException>(() => f.Tasks.MarkDoneAsync(f.Ada.Id, task.Id, CancellationToken.None)); // participants never see cancelled tasks
    }

    [Fact]
    public async Task Done_can_be_taken_back_only_until_an_admin_has_checked_it()
    {
        await using var f = await new Fixture().InitAsync();
        var created = (await f.AssignAsync(f.Ada, [f.Celadon.Id], ["porcelain", "stoneware"])).Created;
        await f.MadeAsync(created[0].Id, f.Ada);
        await f.ApprovedAsync(created[1].Id, f.Ada);

        var undone = await f.Tasks.UndoDoneAsync(f.Ada.Id, created[0].Id, CancellationToken.None);
        var refused = await Assert.ThrowsAsync<ApiProblemException>(() => f.Tasks.UndoDoneAsync(f.Ada.Id, created[1].Id, CancellationToken.None));
        var notHers = await Assert.ThrowsAsync<ApiProblemException>(() => f.Tasks.MarkDoneAsync(f.Ben.Id, created[0].Id, CancellationToken.None));

        Assert.Equal(TileTaskStage.Started, undone.Stage);
        Assert.Equal((409, "task_approved_for_photo"), (refused.Status, refused.Code));
        Assert.Equal(404, notHers.Status);
    }

    [Fact]
    public async Task Overdue_means_past_due_with_no_photo_in_review_or_approved()
    {
        await using var f = await new Fixture().InitAsync();
        var created = (await f.AssignAsync(f.Ada, [f.Celadon.Id], dueOn: TileTaskService.Today)).Created;
        foreach (var t in f.Db.TileTasks) t.DueOn = TileTaskService.Today.AddDays(-2); // time passes
        await f.Db.SaveChangesAsync(); f.Db.ChangeTracker.Clear();
        await f.PhotoAsync(created[0].Id, f.Ada, UploadStatus.NeedsReview);

        var overdue = await TileTaskService.WhereOverdue(TileTaskService.Rows(f.Db.TileTasks), TileTaskService.Today).Select(r => r.Task.Id).ToListAsync();
        var dtos = await Task.WhenAll(created.Select(t => f.GetAsync(t.Id)));

        Assert.Equal(created.Skip(1).Select(t => t.Id).Order(), overdue.Order());
        Assert.Equal([false, true, true], dtos.Select(d => d.Overdue));
    }

    [Fact]
    public async Task A_photo_takes_its_task_only_once_an_admin_has_approved_the_tile()
    {
        await using var f = await new Fixture().InitAsync();
        var created = (await f.AssignAsync(f.Ada, [f.Celadon.Id], ["porcelain", "stoneware", "red_body"])).Created;
        Assert.Equal(["porcelain", "stoneware", "red_body"], created.Select(t => t.ClayBody)); // in the order assigned
        await f.ApprovedAsync(created[0].Id, f.Ada);
        await f.MadeAsync(created[1].Id, f.Ada);
        f.Db.ChangeTracker.Clear();
        var now = DateTimeOffset.UtcNow;

        var byId = await f.Tasks.ForUploadAsync(f.Ada.Id, created[0].Id, null, null, now, CancellationToken.None);
        f.Db.ChangeTracker.Clear();
        var byMatch = await f.Tasks.ForUploadAsync(f.Ada.Id, null, f.Celadon, "porcelain", now, CancellationToken.None);
        f.Db.ChangeTracker.Clear();
        var noMatch = await f.Tasks.ForUploadAsync(f.Ada.Id, null, f.Shino, "stoneware", now, CancellationToken.None);
        var notChecked = await Assert.ThrowsAsync<ApiProblemException>(() => f.Tasks.ForUploadAsync(f.Ben.Id, created[1].Id, null, null, now, CancellationToken.None));
        var notCheckedOldWay = await Assert.ThrowsAsync<ApiProblemException>(() => f.Tasks.ForUploadAsync(f.Ada.Id, null, f.Celadon, "stoneware", now, CancellationToken.None));
        var notMade = await Assert.ThrowsAsync<ApiProblemException>(() => f.Tasks.ForUploadAsync(f.Ada.Id, created[2].Id, null, null, now, CancellationToken.None));
        var wrongBody = await Assert.ThrowsAsync<ApiProblemException>(() => f.Tasks.ForUploadAsync(f.Ada.Id, created[0].Id, null, "red_body", now, CancellationToken.None));
        await f.PhotoAsync(created[0].Id, f.Ada, UploadStatus.NeedsReview);
        var inReview = await Assert.ThrowsAsync<ApiProblemException>(() => f.Tasks.ForUploadAsync(f.Ada.Id, created[0].Id, null, null, now, CancellationToken.None));

        Assert.Equal((created[0].Id, f.Celadon.Id, "porcelain"), (byId!.Id, byId.Recipe.Id, byId.ClayBody));
        Assert.Equal(created[0].Id, byMatch!.Id);
        Assert.Null(noMatch);
        Assert.Equal(("task_not_approved", "task_not_approved", "task_not_done"), (notChecked.Code, notCheckedOldWay.Code, notMade.Code));
        Assert.Equal(("task_clay_body_mismatch", "task_in_review"), (wrongBody.Code, inReview.Code));
    }

    [Fact]
    public async Task The_participants_list_carries_each_recipe_as_mixed()
    {
        await using var f = await new Fixture().InitAsync();
        var baseGlaze = new BaseGlaze { Id = Guid.NewGuid(), OwnerUserId = f.Ada.Id, ParticipantCode = "A01", BaseNumber = 1, Code = "A01-B1", Name = "Leach", Cone = "6", Atmosphere = "oxidation",
            FormulationJson = """[{"rawName":"Silica","amount":30,"unit":"%"},{"rawName":"Kaolin","amount":70,"unit":"%"}]""" };
        f.Db.BaseGlazes.Add(baseGlaze);
        var celadon = await f.Db.Recipes.SingleAsync(r => r.Id == f.Celadon.Id);
        celadon.BaseGlazeId = baseGlaze.Id;
        celadon.AdditionsJson = """[{"rawName":"Ochre","amount":2,"unit":"%"}]""";
        celadon.RecipeJson = """{"full":[{"rawName":"Silica","amount":30,"unit":"%"},{"rawName":"Kaolin","amount":70,"unit":"%"},{"rawName":"Ochre","amount":2,"unit":"%"}]}""";
        await f.Db.SaveChangesAsync();
        var created = (await f.AssignAsync(f.Ada, [f.Celadon.Id], ["porcelain"])).Created;
        var controller = new Glazion.Api.Controllers.MyTasksController(f.Db, f.Tasks, new MatchCheckService(f.Db, null!, new ChemistryDataProvider(TestChemistry.Data())))
        {
            ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext
            {
                HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext
                {
                    User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
                        [new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, f.Ada.Id.ToString())], "test"))
                }
            }
        };

        var before = (IReadOnlyList<TileTaskDto>)((Microsoft.AspNetCore.Mvc.ObjectResult)await controller.List(null, null, null, CancellationToken.None)).Value!;
        var started = await controller.Start(created[0].Id, CancellationToken.None);
        var list = (IReadOnlyList<TileTaskDto>)((Microsoft.AspNetCore.Mvc.ObjectResult)await controller.List(null, null, null, CancellationToken.None)).Value!;

        // Hidden until Start (the admin may still change it), then shown as mixed.
        Assert.Null(Assert.Single(before).Recipe);
        Assert.Equal((TileTaskStage.Started, true), (started.Stage, started.Recipe is not null));
        var recipe = Assert.Single(list).Recipe!;
        Assert.Equal(("A01-B1", "Leach", "6", "oxidation"), (recipe.BaseCode, recipe.BaseName, recipe.Cone, recipe.Atmosphere));
        Assert.Equal(["Silica", "Kaolin"], recipe.Base.Select(m => m.Name));
        Assert.Equal(("Ochre", 2m), (Assert.Single(recipe.Additions).Name, recipe.Additions[0].Amount));
        Assert.Equal(3, recipe.Materials.Count);
        Assert.NotNull((await controller.Get(created[0].Id, CancellationToken.None)).Recipe);
    }

    [Fact]
    public async Task Anyone_photographs_a_tile_once_an_admin_approves_it_and_the_code_lookup_says_which_are_ready()
    {
        await using var f = await new Fixture().InitAsync();
        var created = (await f.AssignAsync(f.Ada, [f.Celadon.Id], ["porcelain", "stoneware", "red_body"])).Created;
        var now = DateTimeOffset.UtcNow;
        async Task<IReadOnlyList<CaptureTileDto>> Tiles(AppUser who) { f.Db.ChangeTracker.Clear(); return await f.Tasks.TilesForCaptureAsync(who.Id, f.Celadon.Id, CancellationToken.None); }

        await f.MadeAsync(created[0].Id, f.Ada);
        f.Db.ChangeTracker.Clear();
        var waiting = await Assert.ThrowsAsync<ApiProblemException>(() => f.Tasks.ForUploadAsync(f.Ben.Id, created[0].Id, null, null, now, CancellationToken.None));
        await f.Tasks.ApproveAsync([created[0].Id], f.Admin.Id, CancellationToken.None);
        await f.MadeAsync(created[1].Id, f.Ada);
        var forBen = await Tiles(f.Ben);
        var forAda = await Tiles(f.Ada);
        var byBen = await f.Tasks.ForUploadAsync(f.Ben.Id, created[0].Id, null, null, now, CancellationToken.None);
        f.Db.ChangeTracker.Clear();
        var byAdmin = await f.Tasks.ForUploadAsync(f.Admin.Id, created[0].Id, null, null, now, CancellationToken.None);
        f.Db.ChangeTracker.Clear();
        var photo = await f.PhotoAsync(created[0].Id, f.Ben, UploadStatus.NeedsReview);
        var inReview = await Tiles(f.Ben);
        var twice = await Assert.ThrowsAsync<ApiProblemException>(() => f.Tasks.ForUploadAsync(f.Admin.Id, created[0].Id, null, null, now, CancellationToken.None));
        photo.Status = UploadStatus.Rejected; photo.ReviewNote = "The code on the back is unreadable";
        f.Db.Uploads.Update(photo); await f.Db.SaveChangesAsync();
        var rejected = await Tiles(f.Ben);

        Assert.Equal("task_not_approved", waiting.Code);
        Assert.Equal([("porcelain", "ready", "ada", false), ("red_body", "not_done", "ada", false), ("stoneware", "not_approved", "ada", false)],
            forBen.Select(t => (t.ClayBody, t.Status, t.MadeByUsername, t.Mine)));
        Assert.Equal(["ready", "not_done", "not_approved"], forAda.Select(t => t.Status)); // her own tiles too wait for the check
        Assert.Equal((created[0].Id, created[0].Id), (byBen!.Id, byAdmin!.Id));
        Assert.Equal(("in_review", "task_in_review"), (inReview[0].Status, twice.Code));
        Assert.Equal(("ready", "The code on the back is unreadable"), (rejected[0].Status, rejected[0].RetakeReason));
    }

    private sealed class MemoryStorage : IR2ObjectStorage
    {
        public Dictionary<string, long> Objects { get; } = new();
        public Task<PresignedUpload> CreatePutUrlAsync(string k, string t, IReadOnlyDictionary<string, string>? m, CancellationToken ct) =>
            Task.FromResult(new PresignedUpload($"https://put.example/{k}", k, DateTimeOffset.UtcNow.AddMinutes(10), new Dictionary<string, string> { ["Content-Type"] = t }));
        public Task<Uri> CreateGetUrlAsync(string k, CancellationToken ct) => CreateGetUrlAsync(k, null, ct);
        public Task<Uri> CreateGetUrlAsync(string k, string? n, CancellationToken ct) => Task.FromResult(new Uri($"https://signed.example/{k}"));
        public Task<StoredObject?> HeadAsync(string k, CancellationToken ct) => Task.FromResult(Objects.TryGetValue(k, out var size) ? new StoredObject(size, "etag", null) : null);
        public Task<Stream> OpenReadAsync(string k, CancellationToken ct) => throw new NotSupportedException();
        public Task CopyAsync(string s, string d, CancellationToken ct) => Task.CompletedTask;
        public Task DeleteAsync(string k, CancellationToken ct) => Task.CompletedTask;
        public Task PutAsync(string k, byte[] c, string t, CancellationToken ct) => Task.CompletedTask;
    }

    [Fact]
    public async Task A_photo_of_an_approved_tile_needs_no_capture_code_and_carries_the_back_of_the_tile()
    {
        await using var f = await new Fixture().InitAsync();
        var task = Assert.Single((await f.AssignAsync(f.Ada, [f.Celadon.Id], ["porcelain"])).Created);
        await f.ApprovedAsync(task.Id, f.Ada);
        f.Db.ChangeTracker.Clear();
        var storage = new MemoryStorage();
        var controller = new Glazion.Api.Controllers.UploadsController(f.Db, storage, new ImageIngestionService(f.Db), new LabCodeService(f.Db), f.Tasks,
            Options.Create(new UploadOptions()), Options.Create(new IngestionOptions()), NullLogger<Glazion.Api.Controllers.UploadsController>.Instance)
        {
            ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext
            {
                HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext
                {
                    User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
                        [new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, f.Ben.Id.ToString())], "test"))
                }
            }
        };
        static T Value<T>(Microsoft.AspNetCore.Mvc.ActionResult<T> result) => result.Value ?? (T)((Microsoft.AspNetCore.Mvc.ObjectResult)result.Result!).Value!;

        var noCode = await Assert.ThrowsAsync<ApiProblemException>(() => controller.Create(new CreateUploadRequest(null, "tile.jpg", "image/jpeg", 1000, null, null, null), CancellationToken.None));
        var upload = Value(await controller.Create(new CreateUploadRequest(null, "tile.jpg", "image/jpeg", 1000, null, null, null, null, task.Id, new UploadBackInput("back.jpg", "image/jpeg", 400)), CancellationToken.None));
        var presign = Value(await controller.Presign(upload.Id, CancellationToken.None));
        storage.Objects[presign.ObjectKey] = 1000;
        var noBack = await Assert.ThrowsAsync<ApiProblemException>(() => controller.Complete(upload.Id, null, CancellationToken.None));
        storage.Objects[presign.Back!.ObjectKey] = 400;
        var done = Value(await controller.Complete(upload.Id, null, CancellationToken.None));
        f.Db.ChangeTracker.Clear();
        var stored = await f.Db.Uploads.Include(u => u.ImageAsset).SingleAsync(u => u.Id == upload.Id);

        Assert.Equal("capture_code_not_active", noCode.Code);
        Assert.Equal((task.Id, f.Celadon.Id, "porcelain"), (upload.TaskId, upload.RecipeId, upload.ClayBody));
        Assert.EndsWith($"{upload.Id:D}/back.jpg", presign.Back.ObjectKey);
        Assert.Equal("back_not_found", noBack.Code);
        Assert.Equal((UploadStatus.Uploaded, (Guid?)null, f.Ben.Id), (done.Status, stored.CaptureCodeId, stored.OwnerUserId));
        Assert.Equal(presign.Back.ObjectKey, stored.ImageAsset!.BackKey);
        Assert.Equal(new Uri($"https://signed.example/{presign.Back.ObjectKey}"), done.BackUrl);
        Assert.Equal(TileTaskStage.InReview, (await f.GetAsync(task.Id)).Stage);
    }
}

public sealed class RegisterBaseWithVariationsTests
{
    [Fact]
    public async Task A_base_registered_with_colorant_stages_sets_up_its_cup_experiment_which_goes_and_comes_back_with_the_base()
    {
        await using var db = new GlazionDbContext(new DbContextOptionsBuilder<GlazionDbContext>().UseInMemoryDatabase("base-stages-" + Guid.NewGuid()).Options);
        var admin = new AppUser { Id = Guid.NewGuid(), Username = "admin", Role = UserRole.Admin, Status = AccountStatus.Active };
        var ryan = new AppUser { Id = Guid.NewGuid(), Username = "ryan", Role = UserRole.User, Status = AccountStatus.Active, ParticipantCode = "P05" };
        var olivia = new AppUser { Id = Guid.NewGuid(), Username = "olivia", Role = UserRole.User, Status = AccountStatus.Active, ParticipantCode = "P04" };
        db.Users.AddRange(admin, ryan, olivia);
        await db.SaveChangesAsync();
        var labCodes = new LabCodeService(db);
        var tasks = new TileTaskService(db, Options.Create(new IngestionOptions()), NullLogger<TileTaskService>.Instance);
        var experiments = new Glazion.Api.Experiments.ExperimentService(db, labCodes, Options.Create(new IngestionOptions()), NullLogger<Glazion.Api.Experiments.ExperimentService>.Instance);
        var controller = new Glazion.Api.Controllers.AdminController(db, null!, labCodes, null!, NullLogger<Glazion.Api.Controllers.AdminController>.Instance)
        {
            ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext
            {
                HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext
                {
                    User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
                        [new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, admin.Id.ToString()), new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, "Admin")], "test"))
                }
            }
        };
        // "Cobalt Tit": cobalt 0.5 to 5% (10 cups), then titanium 2 to 6% (5 steps), then 1% chrome in every cup.
        var stages = new List<Glazion.Api.Experiments.ExperimentStageInput>
        {
            new("Cobalt Carbonate", null, 0.5, 0.5, null, null), new("Titanium Dioxide", "variable", 2, 1, 5, null), new("Chrome Oxide", "constant", null, null, null, 1)
        };
        var cobaltTit = new RegisterBaseGlazeRequest("P05", null, "Cobalt Tit", null, [new MaterialRow("Alberta Slip", 25, "%"), new MaterialRow("Silica", 75, "%")], null, null, "6", "oxidation");
        var result = (RegisterBaseWithVariationsResponse)((Microsoft.AspNetCore.Mvc.ObjectResult)(await controller.RegisterBaseWithVariations(
            new RegisterBaseWithVariationsRequest(cobaltTit, null, null, null, new Glazion.Api.Experiments.BaseExperimentRequest(ryan.Id, 10, null, null, null, null, null, stages)),
            tasks, experiments, CancellationToken.None)).Result!).Value!;

        Assert.Equal("P05-B1", result.Base.Code);
        Assert.Empty(result.Variations); // the codes are given as each step starts
        var e = result.Experiment!;
        Assert.Equal(("Cobalt Tit", "P05-B1", ryan.Id, 10), (e.Name, e.BaseCode, e.ParticipantUserId, e.CupCount));
        Assert.Equal(["Cobalt Carbonate", "Titanium Dioxide", "Chrome Oxide"], e.Stages.Select(s => s.Material));
        Assert.Equal((0, 3), (e.Progress.StepsDone, e.Progress.StepsTotal)); // one round per stage
        Assert.Equal(e.Id, Assert.Single(await experiments.ListAsync(null, false, CancellationToken.None, "p05-b1")).Id);

        // Before the first step everything can change, the cups and who mixes them too; after it, the cups stay.
        e = await experiments.UpdateAsync(e.Id, new(null, null, null, null, null, Cups: 12, DryGrams: 80, WaterGrams: 50, ParticipantUserId: olivia.Id), CancellationToken.None);
        Assert.Equal((12, 80.0, 50.0, olivia.Id), (e.CupCount, e.DryGrams, e.WaterGrams, e.ParticipantUserId));
        Assert.Equal(12, e.Cups.Count);
        Assert.All(e.Cups, c => Assert.Equal(130, c.WetGrams));
        e = await experiments.StartStepAsync(e.Id, olivia.Id, false, CancellationToken.None);
        Assert.Equal("experiment_started", (await Assert.ThrowsAsync<ApiProblemException>(() => experiments.UpdateAsync(e.Id, new(null, null, null, null, null, Cups: 10), CancellationToken.None))).Code);
        await experiments.CancelStepAsync(e.Id, olivia.Id, false, CancellationToken.None);

        // Deleting the base takes its experiment from the participant; restoring brings it back.
        var baseGlaze = await db.BaseGlazes.SingleAsync(b => b.Code == "P05-B1");
        await labCodes.DeleteBaseAsync(baseGlaze, admin.Id, CancellationToken.None);
        Assert.Empty(await experiments.ListAsync(olivia.Id, false, CancellationToken.None));
        await labCodes.RestoreBaseAsync(baseGlaze, admin.Id, CancellationToken.None);
        Assert.Single(await experiments.ListAsync(olivia.Id, false, CancellationToken.None));

        // A refused stage refuses the whole form (no transaction in the test store, so only the refusal is checked here).
        stages[0] = new("Cobalt Carbonate", null, 0.5, 0.5, null, null);
        var tooMany = new Glazion.Api.Experiments.BaseExperimentRequest(ryan.Id, 21, null, null, null, null, null, stages);
        Assert.Equal("validation_error", (await Assert.ThrowsAsync<ApiProblemException>(() => controller.RegisterBaseWithVariations(
            new RegisterBaseWithVariationsRequest(cobaltTit with { Name = "Too many cups" }, null, null, null, tooMany), tasks, experiments, CancellationToken.None))).Code);
    }

    [Fact]
    public async Task The_register_form_makes_the_base_its_series_and_the_tasks_in_one_step()
    {
        await using var db = new GlazionDbContext(new DbContextOptionsBuilder<GlazionDbContext>().UseInMemoryDatabase("base-flow-" + Guid.NewGuid()).Options);
        var admin = new AppUser { Id = Guid.NewGuid(), Username = "admin", Role = UserRole.Admin, Status = AccountStatus.Active };
        var ada = new AppUser { Id = Guid.NewGuid(), Username = "ada", Role = UserRole.User, Status = AccountStatus.Active, ParticipantCode = "A01" };
        db.Users.AddRange(admin, ada);
        await db.SaveChangesAsync();
        var tasks = new TileTaskService(db, Options.Create(new IngestionOptions()), NullLogger<TileTaskService>.Instance);
        var experiments = new Glazion.Api.Experiments.ExperimentService(db, new LabCodeService(db), Options.Create(new IngestionOptions()), NullLogger<Glazion.Api.Experiments.ExperimentService>.Instance);
        var controller = new Glazion.Api.Controllers.AdminController(db, null!, new LabCodeService(db), null!, NullLogger<Glazion.Api.Controllers.AdminController>.Instance)
        {
            ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext
            {
                HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext
                {
                    User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
                        [new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, admin.Id.ToString()), new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, "Admin")], "test"))
                }
            }
        };
        var due = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(7); // a due date must not have passed
        static RegisterVariationRequest Iron(decimal pct) => new($"Bamboo + {pct}% Red Iron Oxide", null, [new MaterialRow("Red Iron Oxide", pct, "%")]);
        var bamboo = new RegisterBaseGlazeRequest(null, ada.Id, "Bamboo", null, [new MaterialRow("Silica", 30, "%"), new MaterialRow("EPK", 20, "%"), new MaterialRow("Whiting", 20, "%"), new MaterialRow("Custer Feldspar", 30, "%")], null, null);

        var result = (RegisterBaseWithVariationsResponse)((Microsoft.AspNetCore.Mvc.ObjectResult)(await controller.RegisterBaseWithVariations(
            new RegisterBaseWithVariationsRequest(bamboo, null, [Iron(1), Iron(2), Iron(3)], new AssignVariationsRequest(ada.Id, ["porcelain"], due, "Dip, two coats")),
            tasks, experiments, CancellationToken.None)).Result!).Value!;

        Assert.Equal("A01-B1", result.Base.Code);
        Assert.Equal(["A01-B1-001", "A01-B1-002", "A01-B1-003"], result.Variations.Select(v => v.Code));
        Assert.Equal(3, result.Assigned!.Created.Count);
        Assert.All(result.Assigned.Created, t => Assert.Equal(("porcelain", ada.Id, (DateOnly?)due), (t.ClayBody, t.AssignedUserId, t.DueOn)));

        // A second series on the same base, without assigning: numbered on from the first.
        var more = (RegisterBaseWithVariationsResponse)((Microsoft.AspNetCore.Mvc.ObjectResult)(await controller.RegisterBaseWithVariations(
            new RegisterBaseWithVariationsRequest(null, "a01-b1", [Iron(4)], null), tasks, experiments, CancellationToken.None)).Result!).Value!;
        Assert.Equal("A01-B1-004", Assert.Single(more.Variations).Code);
        Assert.Null(more.Assigned);

        // The Recipes page: "bamboo" finds the base's variations, each with who has it and how far it has got.
        Task<PagedResult<RecipeDto>> List(string? search = null, Guid? user = null, bool? assigned = null, TileTaskStage? stage = null) =>
            controller.Recipes(null, null, null, search, null, user, assigned, stage, null, tasks, CancellationToken.None);
        var bambooRecipes = await List("bamboo");
        Assert.Equal(["A01-B1-004", "A01-B1-003", "A01-B1-002", "A01-B1-001"], bambooRecipes.Items.Select(r => r.Code));
        Assert.All(bambooRecipes.Items, r => Assert.Equal("Bamboo", r.BaseName));
        var first = bambooRecipes.Items.Single(r => r.Code == "A01-B1-001");
        Assert.Equal(("ada", TileTaskStage.Assigned, "porcelain"), (first.Tasks![0].AssignedUsername, first.Tasks[0].Stage, first.Tasks[0].ClayBody));
        Assert.Empty(bambooRecipes.Items.Single(r => r.Code == "A01-B1-004").Tasks!);
        Assert.Equal(["A01-B1-004"], (await List(assigned: false)).Items.Select(r => r.Code));
        Assert.Equal(3, (await List(user: ada.Id)).Items.Count);
        Assert.Equal(3, (await List(stage: TileTaskStage.Assigned)).Items.Count);
        Assert.Empty((await List(stage: TileTaskStage.Approved)).Items);

        // Either a new base or an existing one, and nothing to assign without variations.
        await Assert.ThrowsAsync<ApiProblemException>(() => controller.RegisterBaseWithVariations(new RegisterBaseWithVariationsRequest(bamboo, "A01-B1", null, null), tasks, experiments, CancellationToken.None));
        await Assert.ThrowsAsync<ApiProblemException>(() => controller.RegisterBaseWithVariations(new RegisterBaseWithVariationsRequest(null, "A01-B1", [], new AssignVariationsRequest(ada.Id, null, null, null)), tasks, experiments, CancellationToken.None));
    }
}
