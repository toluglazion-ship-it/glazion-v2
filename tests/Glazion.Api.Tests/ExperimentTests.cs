using Glazion.Api.Contracts;
using Glazion.Api.Data;
using Glazion.Api.Domain;
using Glazion.Api.Experiments;
using Glazion.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Glazion.Api.Tests;

/// <summary>The arithmetic from the spec's acceptance tests (DEVELOPER_SPEC.md, "Validation and acceptance tests").</summary>
public sealed class ExperimentMathTests
{
    private static readonly ExperimentStage[] Spec =
    [
        new("Cobalt Carbonate", "variable", 0.5, 0.5, 10, 0),
        new("Titanium Dioxide", "constant", 0, 0, 1, 2),
        new("Red Iron Oxide", "variable", 0.5, 0.5, 4, 0),
        new("Copper Carbonate", "constant", 0, 0, 1, 0.5),
        new("Manganese Dioxide", "variable", 0.5, 0.5, 3, 0),
    ];

    [Fact]
    public void Every_stage_is_one_round_across_the_cups_so_ten_cups_and_five_stages_make_fifty_recipes()
    {
        // Meeting 2026-11-09: a stage is added to every cup once, then dipped; a later Series is split across the cups too.
        var plan = ExperimentMath.Plan(Spec);
        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, plan.Select(p => p.StageIndex));
        Assert.Equal(50, plan.Count * 10);
    }

    [Fact]
    public void A_series_split_into_places_gives_each_cup_its_own_amount_and_fixed_gives_every_cup_the_same()
    {
        // 0.5 to 5 split into 10 places: 0.5, 1 … 5. 0.2 to 3 into 10: 0.2, 0.5111 … 3 (the step keeps its decimals).
        ExperimentStage[] stages = [new("Cobalt Carbonate", "variable", 0.5, 0.5, 1, 0), new("Iron Oxide Black", "variable", 0.2, 2.8 / 9, 1, 0), new("Chrome Oxide", "constant", 0, 0, 1, 2)];
        var plan = ExperimentMath.Plan(stages);
        Assert.Equal([0.5, 1, 1.5, 2, 2.5, 3, 3.5, 4, 4.5, 5], Enumerable.Range(1, 10).Select(n => ExperimentMath.Target(stages, plan[0], n)));
        Assert.Equal([0.2, 0.5111, 0.8222, 3], new[] { 1, 2, 3, 10 }.Select(n => ExperimentMath.Target(stages, plan[1], n)));
        Assert.All(Enumerable.Range(1, 10), n => Assert.Equal(2, ExperimentMath.Target(stages, plan[2], n)));
    }

    [Fact]
    public void Cup_one_follows_the_spec_example_and_later_stages_keep_earlier_oxides()
    {
        var plan = ExperimentMath.Plan(Spec);
        var cup = new ExperimentCup { Number = 1, Dry = 100, Water = 60 };

        // Stage 1 at 0.5% cobalt carbonate adds exactly 0.5 g (cup 10, at 5%, adds 5 g).
        var add = ExperimentMath.GramsToAdd(cup, "Cobalt Carbonate", ExperimentMath.Target(Spec, plan[0], 1));
        Assert.Equal(0.5, add, 9);
        Assert.Equal(5, ExperimentMath.GramsToAdd(new ExperimentCup { Number = 10, Dry = 100, Water = 60 }, "Cobalt Carbonate", ExperimentMath.Target(Spec, plan[0], 10)), 9);
        ExperimentMath.Add(cup, "Cobalt Carbonate", add);
        Assert.Equal(160.5, cup.Wet, 9);

        // Three tiles take 15 g: 145.5 g is left, so 90.6542056 g of dry base.
        ExperimentMath.Keep(cup, ExperimentMath.Retention(160.5, 145.5));
        Assert.Equal(145.5, cup.Wet, 9);
        Assert.Equal(90.6542056, cup.Dry, 6);
        Assert.Equal(0.5, cup.Percentages()["Cobalt Carbonate"], 9); // dipping never changes the recipe

        // Stage 2: 2% titanium dioxide of the dry base left, about 1.8130841 g.
        Assert.Equal(1.8130841, ExperimentMath.GramsToAdd(cup, "Titanium Dioxide", ExperimentMath.Target(Spec, plan[1], 1)), 6);

        // Stage 3 is a series across the cups: cup 1 gets 0.5%, cup 4 gets 2%, without resetting the cobalt; a lower target than what is in adds nothing.
        Assert.Equal(new[] { 0.5, 1, 1.5, 2 }, new[] { 1, 2, 3, 4 }.Select(n => ExperimentMath.Target(Spec, plan[2], n)));
        ExperimentMath.Add(cup, "Red Iron Oxide", ExperimentMath.GramsToAdd(cup, "Red Iron Oxide", 1));
        Assert.Equal(0, ExperimentMath.GramsToAdd(cup, "Red Iron Oxide", 0.5));
        Assert.Equal(0.5, cup.Percentages()["Cobalt Carbonate"], 9);
    }

    [Fact]
    public void Fresh_glaze_takes_every_material_at_the_cups_percentages()
    {
        var cup = new ExperimentCup { Number = 1, Dry = 20, Water = 12, Additives = new(StringComparer.OrdinalIgnoreCase) { ["Cobalt Carbonate"] = 0.1, ["Titanium Dioxide"] = 0.4 } };
        var adds = ExperimentMath.RefillAdds(cup, 50);
        Assert.Equal(0.25, adds["Cobalt Carbonate"], 9); // 0.5% of 50 g
        Assert.Equal(1, adds["Titanium Dioxide"], 9);    // 2% of 50 g
    }
}

public sealed class ExperimentServiceTests
{
    private sealed class Fixture : IAsyncDisposable
    {
        public GlazionDbContext Db { get; } = new(new DbContextOptionsBuilder<GlazionDbContext>().UseInMemoryDatabase("experiments-" + Guid.NewGuid()).Options);
        public AppUser Admin { get; } = new() { Id = Guid.NewGuid(), Username = "admin", Role = UserRole.Admin, Status = AccountStatus.Active };
        public AppUser Emily { get; } = new() { Id = Guid.NewGuid(), Username = "emily", Role = UserRole.User, Status = AccountStatus.Active, ParticipantCode = "P02" };
        public LabCodeService LabCodes { get; }
        public ExperimentService Experiments { get; }
        public BaseGlaze Base { get; private set; } = null!;

        public Fixture()
        {
            LabCodes = new LabCodeService(Db);
            Experiments = new ExperimentService(Db, LabCodes, Options.Create(new IngestionOptions()), NullLogger<ExperimentService>.Instance);
        }

        public async Task<Fixture> InitAsync()
        {
            Db.Users.AddRange(Admin, Emily);
            await Db.SaveChangesAsync();
            Base = await LabCodes.RegisterBaseAsync(Emily, "Clear", null, [new MaterialRow("Silica", 30, "%"), new MaterialRow("Kaolin", 70, "%")], null, null, Admin.Id, CancellationToken.None);
            return this;
        }

        public Task<ExperimentDto> CreateAsync(int cups = 3, IReadOnlyList<string>? clayBodies = null) => Experiments.CreateAsync(new CreateExperimentRequest(null, Base.Code, Emily.Id, cups, 100, 60, 3, clayBodies,
            10, [new ExperimentStageInput("Cobalt Carbonate", null, 0.5, 0.5, null, null), new ExperimentStageInput("Titanium Dioxide", "constant", null, null, null, 2),
                new ExperimentStageInput("Red Iron Oxide", "variable", 0.5, 0.5, 2, null)]), Admin.Id, CancellationToken.None);

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    [Fact]
    public async Task Each_step_gives_every_cup_its_grams_and_code_and_dipping_makes_tiles_that_wait_for_the_check()
    {
        await using var f = await new Fixture().InitAsync();
        var e = await f.CreateAsync();
        Assert.Equal((0, 3), (e.Progress.StepsDone, e.Progress.StepsTotal)); // one round per stage
        Assert.Equal([0.5, 1, 1.5], e.Current!.Cups.Select(c => c.TargetPercent!.Value)); // a preview before Start
        Assert.Null(e.Current.Id);

        e = await f.Experiments.StartStepAsync(e.Id, f.Emily.Id, false, CancellationToken.None);
        Assert.NotNull(e.Current!.Id);
        Assert.Equal(["P02-B1-001", "P02-B1-002", "P02-B1-003"], e.Current.Cups.Select(c => c.RecipeCode));
        Assert.Equal([0.5, 1.0, 1.5], e.Current.Cups.Select(c => c.AddGrams!.Value));
        Assert.Equal("B1 + 0.5% Cobalt Carbonate", e.Current.Cups[0].RecipeName);
        // Saying Start twice returns the open step.
        Assert.Equal(e.Current.Id, (await f.Experiments.StartStepAsync(e.Id, f.Emily.Id, false, CancellationToken.None)).Current!.Id);

        // Weighed cup 1: 145.5 g of 160.5 left. Every cup keeps that share; the tiles are made, waiting for the admin.
        e = await f.Experiments.DippedAsync(e.Id, new DippedRequest(1, 145.5), f.Emily.Id, false, CancellationToken.None);
        Assert.Equal(1, e.Progress.StepsDone);
        Assert.Equal(90.65, e.Cups[0].DryGrams);
        var tasks = await f.Db.TileTasks.AsNoTracking().ToListAsync();
        Assert.Equal(9, tasks.Count); // 3 cups × 3 clay bodies
        Assert.All(tasks, t => Assert.Equal((f.Emily.Id, true, true, (DateTimeOffset?)null), (t.AssignedUserId, t.StartedAt != null, t.DoneAt != null, t.ApprovedAt)));

        // Next: 2% titanium dioxide of what is left in each cup.
        Assert.Equal(1.813, e.Current!.Cups[0].AddGrams);
        e = await f.Experiments.StartStepAsync(e.Id, f.Emily.Id, false, CancellationToken.None);
        Assert.Equal("B1 + 0.5% Cobalt Carbonate + 2% Titanium Dioxide", e.Current!.Cups[0].RecipeName);
        var recipe = await f.Db.Recipes.AsNoTracking().SingleAsync(r => r.Code == e.Current.Cups[0].RecipeCode);
        Assert.Contains("Titanium Dioxide", recipe.AdditionsJson);
    }

    [Fact]
    public async Task The_admin_can_change_stages_not_started_and_one_clay_body_makes_one_task()
    {
        await using var f = await new Fixture().InitAsync();
        var e = await f.CreateAsync(cups: 2, clayBodies: ["porcelain", "porcelain", "porcelain"]);
        e = await f.Experiments.StartStepAsync(e.Id, f.Emily.Id, false, CancellationToken.None);
        e = await f.Experiments.DippedAsync(e.Id, new DippedRequest(2, 140), f.Emily.Id, false, CancellationToken.None);
        Assert.Equal(2, await f.Db.TileTasks.CountAsync()); // three porcelain tiles per cup: one tile task each

        // Stage 1 has started; stage 2 has not, so it can become tin oxide (meeting 2026-11-02).
        var stages = new List<ExperimentStageInput>
        {
            new("Cobalt Carbonate", null, 0.5, 0.5, null, null), new("Tin Oxide", "constant", null, null, null, 3), new("Red Iron Oxide", "variable", 0.5, 0.5, 2, null)
        };
        e = await f.Experiments.UpdateAsync(e.Id, new UpdateExperimentRequest(null, stages, null, null, null), CancellationToken.None);
        Assert.Equal("Tin Oxide", e.Stages[1].Material);
        stages[0] = new("Cobalt Carbonate", null, 1, 1, null, null);
        Assert.Equal("stage_started", (await Assert.ThrowsAsync<ApiProblemException>(() => f.Experiments.UpdateAsync(e.Id, new UpdateExperimentRequest(null, stages, null, null, null), CancellationToken.None))).Code);
    }

    [Fact]
    public async Task A_low_cup_sits_out_until_fresh_glaze_comes_with_its_materials_and_a_started_step_can_be_undone()
    {
        await using var f = await new Fixture().InitAsync();
        var e = await f.CreateAsync(cups: 2);
        e = await f.Experiments.StartStepAsync(e.Id, f.Emily.Id, false, CancellationToken.None);
        // A heavy dip: 30 g left of about 160, so another like it would leave under the 10 g reserve.
        e = await f.Experiments.DippedAsync(e.Id, new DippedRequest(1, 30), f.Emily.Id, false, CancellationToken.None);
        Assert.All(e.Cups, c => Assert.True(c.NeedsGlaze));
        Assert.Equal("cups_need_glaze", (await Assert.ThrowsAsync<ApiProblemException>(() => f.Experiments.StartStepAsync(e.Id, f.Emily.Id, false, CancellationToken.None))).Code);

        e = await f.Experiments.RefillAsync(e.Id, 1, new RefillRequest(80, 48), f.Emily.Id, false, CancellationToken.None);
        var refill = e.History.Last();
        Assert.Equal("refill", refill.Kind);
        Assert.Equal(0.4, refill.Cups[0].RefillAdds!.Single().Grams); // 0.5% cobalt of 80 g fresh dry glaze
        Assert.False(e.Cups[0].NeedsGlaze);

        // Cup 2 sits this step out; cup 1 gets its code.
        e = await f.Experiments.StartStepAsync(e.Id, f.Emily.Id, false, CancellationToken.None);
        Assert.Equal([false, true], e.Current!.Cups.Select(c => c.Skipped));
        var code = e.Current.Cups[0].RecipeCode;
        e = await f.Experiments.CancelStepAsync(e.Id, f.Emily.Id, false, CancellationToken.None);
        Assert.Null(e.Current!.Id); // back to the preview
        Assert.NotNull((await f.Db.Recipes.AsNoTracking().SingleAsync(r => r.Code == code)).DeletedAt);
        Assert.Equal(1, e.Progress.StepsDone);
    }

    [Fact]
    public async Task A_stage_finishes_without_weighing_by_taking_the_placeholder_for_each_tile_and_stage_one_can_be_fixed()
    {
        await using var f = await new Fixture().InitAsync();
        // Stage 1 Fixed (2% in both cups), then a series split across the cups.
        var e = await f.Experiments.CreateAsync(new CreateExperimentRequest(null, f.Base.Code, f.Emily.Id, 2, 100, 60, 3, null, 10,
            [new ExperimentStageInput("Chrome Oxide", "constant", null, null, null, 2), new ExperimentStageInput("Cobalt Carbonate", "variable", 1, 1, null, null)]), f.Admin.Id, CancellationToken.None);
        Assert.Equal([2.0, 2.0], e.Current!.Cups.Select(c => c.TargetPercent!.Value));
        e = await f.Experiments.StartStepAsync(e.Id, f.Emily.Id, false, CancellationToken.None);
        Assert.Equal(162, e.Current!.Cups[0].WetBeforeGrams);

        // Stage 1 completed, nobody weighed: 3 tiles × 5 g come out of each cup, and stage 2 opens.
        e = await f.Experiments.DippedAsync(e.Id, new DippedRequest(), f.Emily.Id, false, CancellationToken.None);
        var done = e.History.Single();
        Assert.Equal((null, null), (done.WeighedCup, done.WeighedGrams));
        Assert.All(done.Cups, c => Assert.Equal(162 - 3 * ExperimentService.PlaceholderGramsPerTile, c.WetAfterGrams));
        Assert.All(e.Cups, c => Assert.Equal(2, c.Materials.Single().Percent)); // dipping never changes the recipe
        Assert.Equal(6, await f.Db.TileTasks.CountAsync()); // 2 cups × 3 clay bodies, waiting for the admin's check
        Assert.Equal([1.0, 2.0], e.Current!.Cups.Select(c => c.TargetPercent!.Value)); // stage 2: its own amount per cup
        Assert.Equal("1% in cup 1 up to 2% in cup 2", e.Stages[1].Summary);
    }

    [Fact]
    public async Task A_series_can_go_down_from_100_to_1_but_not_below_0()
    {
        await using var f = await new Fixture().InitAsync();
        // 100 to 1 split into 10 places: 100, 89 … 1.
        var e = await f.Experiments.CreateAsync(new CreateExperimentRequest(null, f.Base.Code, f.Emily.Id, 10, 100, 60, 3, null, 10,
            [new ExperimentStageInput("Cobalt Carbonate", "variable", 100, -11, null, null)]), f.Admin.Id, CancellationToken.None);
        Assert.Equal([100.0, 89, 1], new[] { 0, 1, 9 }.Select(i => e.Current!.Cups[i].TargetPercent!.Value));
        Assert.Equal("100% in cup 1 down to 1% in cup 10", e.Stages[0].Summary);
        Assert.Equal("validation_error", (await Assert.ThrowsAsync<ApiProblemException>(() => f.Experiments.CreateAsync(new CreateExperimentRequest(null, f.Base.Code, f.Emily.Id, 10, 100, 60, 3, null, 10,
            [new ExperimentStageInput("Cobalt Carbonate", "variable", 5, -1, null, null)]), f.Admin.Id, CancellationToken.None))).Code);
    }

    [Fact]
    public async Task A_user_created_without_a_code_gets_the_next_free_one()
    {
        await using var f = await new Fixture().InitAsync(); // Emily is P02
        f.Db.Users.Add(new AppUser { Id = Guid.NewGuid(), Username = "hannah", Role = UserRole.User, Status = AccountStatus.Active, ParticipantCode = "P06" });
        f.Db.Users.Add(new AppUser { Id = Guid.NewGuid(), Username = "ana", Role = UserRole.User, Status = AccountStatus.Active, ParticipantCode = "A03" });
        await f.Db.SaveChangesAsync();
        Assert.Equal("P07", await f.LabCodes.NextParticipantCodeAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Only_the_participant_sees_their_experiment_and_the_weight_must_be_less_than_before()
    {
        await using var f = await new Fixture().InitAsync();
        var e = await f.CreateAsync();
        Assert.Equal("experiment_not_found", (await Assert.ThrowsAsync<ApiProblemException>(() => f.Experiments.GetAsync(e.Id, f.Admin.Id, CancellationToken.None))).Code);
        e = await f.Experiments.StartStepAsync(e.Id, f.Emily.Id, false, CancellationToken.None);
        Assert.Equal("validation_error", (await Assert.ThrowsAsync<ApiProblemException>(() => f.Experiments.DippedAsync(e.Id, new DippedRequest(1, 170), f.Emily.Id, false, CancellationToken.None))).Code);
    }
}
