using System.Globalization;
using System.Text.Json;
using Glazion.Api.Contracts;
using Glazion.Api.Data;
using Glazion.Api.Domain;
using Glazion.Api.Infrastructure;
using Glazion.Api.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Glazion.Api.Experiments;

/// <summary>
/// Cup experiments (meeting 2026-11-02; docs/features/cup-experiments.md). An admin sets one up for a participant: a base, the
/// cups, the tiles dipped after each change and up to five stages. The participant then works step by step: Start gives
/// every cup the grams to add and its own recipe code, then they dip, weigh one cup and say so, and the next step opens
/// at once. The dipped tiles become tile tasks already marked made, so they wait for the admin's check and then a photo.
/// </summary>
public sealed class ExperimentService(GlazionDbContext db, LabCodeService labCodes, IOptions<IngestionOptions> ingestion, ILogger<ExperimentService> logger)
{
    public const int MaxCups = 20;

    /// <summary>
    /// Glaze one dipped tile takes out of a cup, in grams, while participants don't weigh (meeting 2026-11-09). A
    /// placeholder until the measured leftovers after stages 1, 2 and 3 replace it.
    /// </summary>
    public const double PlaceholderGramsPerTile = 5;
    public const int MaxStages = 5;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>A cup as stored in a step.</summary>
    private sealed class StepCup
    {
        public int Number { get; set; }
        public double? Target { get; set; }
        public double? Add { get; set; }
        public double? WetBefore { get; set; }
        public double? WetAfter { get; set; }
        public Guid? RecipeId { get; set; }
        public string? RecipeCode { get; set; }
        public string? RecipeName { get; set; }
        public bool Skipped { get; set; }
        public double? FreshDry { get; set; }
        public double? FreshWater { get; set; }
        public Dictionary<string, double>? Adds { get; set; }
    }

    // ---- admin ----

    public async Task<ExperimentDto> CreateAsync(CreateExperimentRequest request, Guid adminId, CancellationToken cancellationToken)
    {
        var baseGlaze = await BaseAsync(request.BaseCode, cancellationToken);
        var participant = await ParticipantAsync(request.ParticipantUserId, cancellationToken);
        var cups = request.Cups ?? 10;
        if (cups is < 1 or > MaxCups) throw ApiProblems.Validation("cups", $"Use between 1 and {MaxCups} cups.");
        var dry = request.DryGrams ?? 100;
        var water = request.WaterGrams ?? 60;
        if (dry <= 0 || dry > 100_000) throw ApiProblems.Validation("dryGrams", "Give the dry glaze per cup in grams, above 0.");
        if (water < 0 || water > 100_000) throw ApiProblems.Validation("waterGrams", "Give the water per cup in grams, 0 or more.");
        var stages = Stages(request.Stages);
        CheckAmounts(stages, cups);
        var name = string.IsNullOrWhiteSpace(request.Name) ? $"{baseGlaze.Code}: {string.Join(", ", stages.Select(s => s.Material))}" : request.Name.Trim();
        if (name.Length > 180) throw ApiProblems.Validation("name", "Keep the name to 180 characters.");

        var now = DateTimeOffset.UtcNow;
        var experiment = new Experiment
        {
            Id = Guid.CreateVersion7(), Name = name, BaseGlazeId = baseGlaze.Id, ParticipantUserId = participant.Id, CupCount = cups, DryGrams = dry, WaterGrams = water,
            TilesPerStep = TilesPerStep(request.TilesPerStep), ClayBodiesJson = JsonSerializer.Serialize(ClayBodies(request.ClayBodies), Json),
            MinReserveGrams = Reserve(request.MinReserveGrams), StagesJson = JsonSerializer.Serialize(stages, Json),
            CupsJson = JsonSerializer.Serialize(Enumerable.Range(1, cups).Select(n => new ExperimentCup { Number = n, Dry = dry, Water = water }).ToList(), Json),
            CreatedBy = adminId, CreatedAt = now, UpdatedAt = now
        };
        db.Experiments.Add(experiment);
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Admin {AdminId} set up experiment {ExperimentId} on {Base} for {UserId}: {Cups} cups, {Stages} stages", adminId, experiment.Id, baseGlaze.Code, participant.Id, cups, stages.Count);
        return await GetAsync(experiment.Id, null, cancellationToken);
    }

    /// <summary>The admin can change what hasn't started yet (meeting 2026-11-02): later stages, the tiles, the reserve, the name.</summary>
    public async Task<ExperimentDto> UpdateAsync(Guid id, UpdateExperimentRequest request, CancellationToken cancellationToken)
    {
        var experiment = await FindAsync(id, null, cancellationToken);
        if (request.Name is not null)
        {
            if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 180) throw ApiProblems.Validation("name", "A name of at most 180 characters is required.");
            experiment.Name = request.Name.Trim();
        }
        if (request.Stages is not null)
        {
            var old = StagesOf(experiment);
            var stages = Stages(request.Stages);
            var started = experiment.Steps.Where(s => s.Kind == "mix").ToList();
            for (var i = 0; i < old.Count; i++)
            {
                var levelsStarted = started.Where(s => s.StageIndex == i).Select(s => s.Level ?? 1).DefaultIfEmpty(0).Max();
                if (levelsStarted == 0) continue;
                if (i >= stages.Count || !SameExceptLevels(old[i], stages[i]))
                    throw ApiProblems.Conflict("stage_started", $"Stage {i + 1} ({old[i].Material}) has started, so it stays as it is. Change the stages after it.");
            }
            experiment.StagesJson = JsonSerializer.Serialize(stages, Json);
        }
        if (request.TilesPerStep is not null) experiment.TilesPerStep = TilesPerStep(request.TilesPerStep);
        if (request.ClayBodies is not null) experiment.ClayBodiesJson = JsonSerializer.Serialize(ClayBodies(request.ClayBodies), Json);
        if (request.MinReserveGrams is not null) experiment.MinReserveGrams = Reserve(request.MinReserveGrams);

        // The cups are mixed when the first step starts: until then, how many, how much glaze and who mixes them can change.
        var cups = request.Cups ?? experiment.CupCount;
        var dry = request.DryGrams ?? experiment.DryGrams;
        var water = request.WaterGrams ?? experiment.WaterGrams;
        var participantId = request.ParticipantUserId ?? experiment.ParticipantUserId;
        if (cups != experiment.CupCount || dry != experiment.DryGrams || water != experiment.WaterGrams || participantId != experiment.ParticipantUserId)
        {
            if (experiment.Steps.Count > 0)
                throw ApiProblems.Conflict("experiment_started", "The cups were mixed when the first step started, so the number of cups, the glaze in them and who mixes them stay as they are.");
            if (cups is < 1 or > MaxCups) throw ApiProblems.Validation("cups", $"Use between 1 and {MaxCups} cups.");
            if (dry <= 0 || dry > 100_000) throw ApiProblems.Validation("dryGrams", "Give the dry glaze per cup in grams, above 0.");
            if (water < 0 || water > 100_000) throw ApiProblems.Validation("waterGrams", "Give the water per cup in grams, 0 or more.");
            if (participantId != experiment.ParticipantUserId) experiment.ParticipantUserId = (await ParticipantAsync(participantId, cancellationToken)).Id;
            experiment.CupCount = cups;
            experiment.DryGrams = dry;
            experiment.WaterGrams = water;
            experiment.CupsJson = JsonSerializer.Serialize(Enumerable.Range(1, cups).Select(n => new ExperimentCup { Number = n, Dry = dry, Water = water }).ToList(), Json);
        }
        CheckAmounts(StagesOf(experiment), experiment.CupCount);
        experiment.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return await GetAsync(id, null, cancellationToken);
    }

    /// <summary>Takes the experiment away from the participant. Its recipes and tiles stay (delete those on their own); it can be restored.</summary>
    public async Task<ExperimentDto> DeleteAsync(Guid id, bool restore, CancellationToken cancellationToken)
    {
        var experiment = await db.Experiments.SingleOrDefaultAsync(x => x.Id == id, cancellationToken) ?? throw NotFound();
        experiment.DeletedAt = restore ? null : experiment.DeletedAt ?? DateTimeOffset.UtcNow;
        experiment.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return await GetAsync(id, null, cancellationToken);
    }

    public async Task<IReadOnlyList<ExperimentDto>> ListAsync(Guid? participantUserId, bool deleted, CancellationToken cancellationToken, string? baseCode = null)
    {
        var query = db.Experiments.AsNoTracking().Where(x => deleted ? x.DeletedAt != null : x.DeletedAt == null);
        if (participantUserId is { } userId) query = query.Where(x => x.ParticipantUserId == userId);
        if (!string.IsNullOrWhiteSpace(baseCode))
        {
            var code = baseCode.Trim().ToUpperInvariant();
            query = query.Where(x => x.BaseGlaze.Code == code);
        }
        var ids = await query.OrderByDescending(x => x.CreatedAt).Select(x => x.Id).Take(200).ToListAsync(cancellationToken);
        var result = new List<ExperimentDto>();
        foreach (var id in ids) result.Add(await GetAsync(id, null, cancellationToken));
        return result;
    }

    // ---- the participant's steps (the admin may do them too) ----

    /// <summary>
    /// Starts the next step: every cup that still has enough glaze gets the grams to add and its own new recipe code.
    /// A cup marked as needing glaze is left out of this step. Saying it twice returns the open step.
    /// </summary>
    public async Task<ExperimentDto> StartStepAsync(Guid id, Guid userId, bool isAdmin, CancellationToken cancellationToken)
    {
        var experiment = await FindAsync(id, isAdmin ? null : userId, cancellationToken);
        if (experiment.Steps.Any(s => s.Kind == "mix" && s.DippedAt is null)) return await GetAsync(id, null, cancellationToken);
        var stages = StagesOf(experiment);
        var next = NextStep(experiment, stages) ?? throw ApiProblems.Conflict("experiment_finished", "Every step of this experiment is done.");
        var cups = CupsOf(experiment);
        var included = cups.Where(c => !c.NeedsGlaze).ToList();
        if (included.Count == 0) throw ApiProblems.Conflict("cups_need_glaze", "Every cup is too low for another dip. Add fresh glaze to a cup first.");

        // The grams first, then one recipe per cup with its whole composition so far.
        var stepCups = new List<StepCup>();
        var inputs = new List<VariationInput>();
        var baseGlaze = experiment.BaseGlaze;
        foreach (var cup in cups)
        {
            if (cup.NeedsGlaze) { stepCups.Add(new StepCup { Number = cup.Number, Skipped = true }); continue; }
            var target = ExperimentMath.Target(stages, next, cup.Number);
            var add = ExperimentMath.GramsToAdd(cup, next.Material, target);
            ExperimentMath.Add(cup, next.Material, add);
            var composition = cup.Percentages();
            var rows = stages.Select(s => s.Material).Where(m => composition.TryGetValue(m, out var p) && p > 0)
                .Select(m => new MaterialRow(m, (decimal)ExperimentMath.Round(composition[m]), "%")).ToList();
            var name = $"B{baseGlaze.BaseNumber} + {(rows.Count == 0 ? "nothing added" : string.Join(" + ", rows.Select(r => $"{Percent((double)r.Amount)}% {r.RawName}")))}";
            inputs.Add(new VariationInput(name, $"{experiment.Name}: cup {cup.Number}, stage {next.StageIndex + 1}.", rows));
            stepCups.Add(new StepCup { Number = cup.Number, Target = target, Add = add, WetBefore = cup.Wet });
        }
        var recipes = await labCodes.RegisterVariationsAsync(baseGlaze, inputs, experiment.ParticipantUserId, cancellationToken);
        foreach (var (stepCup, recipe) in stepCups.Where(c => !c.Skipped).Zip(recipes))
        {
            stepCup.RecipeId = recipe.Id;
            stepCup.RecipeCode = recipe.Code;
            stepCup.RecipeName = recipe.Name;
        }

        db.ExperimentSteps.Add(new ExperimentStep
        {
            Id = Guid.CreateVersion7(), ExperimentId = experiment.Id, Ordinal = experiment.Steps.Count + 1, Kind = "mix", StageIndex = next.StageIndex, Level = next.Level,
            Material = next.Material, CupsJson = JsonSerializer.Serialize(stepCups, Json), ActorUserId = userId, StartedAt = DateTimeOffset.UtcNow
        });
        experiment.CupsJson = JsonSerializer.Serialize(cups, Json);
        experiment.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return await GetAsync(id, null, cancellationToken);
    }

    /// <summary>Undoes a step started by mistake, before dipping: the grams come off the cups and its new recipes are deleted.</summary>
    public async Task<ExperimentDto> CancelStepAsync(Guid id, Guid userId, bool isAdmin, CancellationToken cancellationToken)
    {
        var experiment = await FindAsync(id, isAdmin ? null : userId, cancellationToken);
        var open = experiment.Steps.SingleOrDefault(s => s.Kind == "mix" && s.DippedAt is null) ?? throw ApiProblems.Conflict("no_open_step", "No step is waiting to be dipped.");
        var cups = CupsOf(experiment);
        var now = DateTimeOffset.UtcNow;
        foreach (var stepCup in JsonSerializer.Deserialize<List<StepCup>>(open.CupsJson, Json) ?? [])
        {
            if (stepCup.Skipped) continue;
            if (cups.FirstOrDefault(c => c.Number == stepCup.Number) is { } cup) ExperimentMath.Add(cup, open.Material!, -(stepCup.Add ?? 0));
            if (stepCup.RecipeId is { } recipeId && await db.Recipes.SingleOrDefaultAsync(r => r.Id == recipeId, cancellationToken) is { } recipe)
                await labCodes.DeleteRecipeAsync(recipe, userId, now, cancellationToken);
        }
        db.ExperimentSteps.Remove(open);
        experiment.CupsJson = JsonSerializer.Serialize(cups, Json);
        experiment.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        return await GetAsync(id, null, cancellationToken);
    }

    /// <summary>
    /// The stage is done: the tiles are dipped, each cup's tiles become tile tasks already made (waiting for the admin's
    /// check), and the next stage opens. With a cup weighed, that cup's share left is applied to every cup. Without one
    /// (meeting 2026-11-09: no weighing for now), each tile is taken to use <see cref="PlaceholderGramsPerTile"/> g of
    /// glaze until the real figures are measured. A cup that would drop below the reserve with another dip like this one
    /// is marked as needing fresh glaze.
    /// </summary>
    public async Task<ExperimentDto> DippedAsync(Guid id, DippedRequest request, Guid userId, bool isAdmin, CancellationToken cancellationToken)
    {
        var experiment = await FindAsync(id, isAdmin ? null : userId, cancellationToken);
        var open = experiment.Steps.SingleOrDefault(s => s.Kind == "mix" && s.DippedAt is null) ?? throw ApiProblems.Conflict("no_open_step", "Start the step first: add the materials, then dip.");
        var stepCups = JsonSerializer.Deserialize<List<StepCup>>(open.CupsJson, Json) ?? [];
        StepCup? weighed = null;
        if (request.WetGrams is { } wet)
        {
            weighed = stepCups.FirstOrDefault(c => c.Number == request.CupNumber && !c.Skipped)
                ?? throw ApiProblems.Validation("cupNumber", "Weigh one of the cups dipped in this step.");
            if (!(wet > 0)) throw ApiProblems.Validation("wetGrams", "Enter the weight of the glaze left in the cup, in grams.");
            if (wet >= weighed.WetBefore)
                throw ApiProblems.Validation("wetGrams", $"Cup {weighed.Number} held about {Grams(weighed.WetBefore ?? 0)} g before dipping, so it must weigh less now. Take the empty cup's weight off.");
        }

        // Weighed: the share left in that cup. Not weighed: each cup loses the placeholder grams for its tiles.
        var used = PlaceholderGramsPerTile * experiment.TilesPerStep;
        double ShareLeft(double wetBefore) => weighed is not null
            ? ExperimentMath.Retention(weighed.WetBefore!.Value, request.WetGrams!.Value)
            : wetBefore > 0 ? Math.Max(0, wetBefore - used) / wetBefore : 0;
        var cups = CupsOf(experiment);
        var now = DateTimeOffset.UtcNow;
        var clayBodies = JsonSerializer.Deserialize<List<string>>(experiment.ClayBodiesJson, Json) ?? [];
        var shares = new List<double>();
        foreach (var stepCup in stepCups.Where(c => !c.Skipped))
        {
            var cup = cups.First(c => c.Number == stepCup.Number);
            var retention = ShareLeft(cup.Wet);
            shares.Add(retention);
            ExperimentMath.Keep(cup, retention);
            stepCup.WetAfter = cup.Wet;
            // Another dip like this one takes the same again; below the reserve the cup needs fresh glaze first.
            cup.NeedsGlaze = (weighed is not null ? cup.Wet * retention : cup.Wet - used) < experiment.MinReserveGrams;
            if (stepCup.RecipeId is not { } recipeId) continue;
            foreach (var clayBody in clayBodies.Distinct())
            {
                var task = new TileTask
                {
                    Id = Guid.CreateVersion7(), RecipeId = recipeId, ClayBody = clayBody, AssignedUserId = experiment.ParticipantUserId, AssignedAt = open.StartedAt,
                    StartedAt = open.StartedAt, DoneAt = now, CreatedBy = userId, CreatedAt = now, UpdatedAt = now
                };
                task.Events.Add(TileTaskService.Event(task, TileTaskEventKind.Assigned, userId, open.StartedAt, toUserId: experiment.ParticipantUserId, note: $"{experiment.Name}, cup {cup.Number}"));
                task.Events.Add(TileTaskService.Event(task, TileTaskEventKind.MarkedDone, userId, now, note: $"Dipped from cup {cup.Number}"));
                db.TileTasks.Add(task);
            }
        }
        open.CupsJson = JsonSerializer.Serialize(stepCups, Json);
        open.DippedAt = now;
        open.WeighedCup = weighed?.Number;
        open.WeighedGrams = request.WetGrams;
        open.Retention = shares.Count > 0 ? shares.Average() : null;
        experiment.CupsJson = JsonSerializer.Serialize(cups, Json);
        experiment.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Experiment {ExperimentId} step {Ordinal} dipped ({How}): {Loss:P1} of each cup used", experiment.Id, open.Ordinal,
            weighed is null ? "placeholder" : $"cup {weighed.Number} weighed", 1 - (open.Retention ?? 1));
        return await GetAsync(id, null, cancellationToken);
    }

    /// <summary>
    /// Fresh glaze poured into a cup that ran low (with the admin's help): Glazion gives the grams of every material
    /// already in the cup to add with it, so the cup stays the same recipe, and the cup takes part in the next step again.
    /// </summary>
    public async Task<ExperimentDto> RefillAsync(Guid id, int cupNumber, RefillRequest request, Guid userId, bool isAdmin, CancellationToken cancellationToken)
    {
        var experiment = await FindAsync(id, isAdmin ? null : userId, cancellationToken);
        if (experiment.Steps.Any(s => s.Kind == "mix" && s.DippedAt is null))
            throw ApiProblems.Conflict("step_open", "Finish the open step first (dip and weigh), then add fresh glaze.");
        if (!(request.DryGrams > 0)) throw ApiProblems.Validation("dryGrams", "Give the fresh dry glaze in grams, above 0.");
        if (request.WaterGrams < 0) throw ApiProblems.Validation("waterGrams", "Give the water in grams, 0 or more.");
        var cups = CupsOf(experiment);
        var cup = cups.FirstOrDefault(c => c.Number == cupNumber) ?? throw ApiProblems.Validation("cupNumber", $"There is no cup {cupNumber}.");
        var adds = ExperimentMath.RefillAdds(cup, request.DryGrams);
        cup.Dry += request.DryGrams;
        cup.Water += request.WaterGrams;
        foreach (var (material, grams) in adds) ExperimentMath.Add(cup, material, grams);
        cup.NeedsGlaze = false;
        db.ExperimentSteps.Add(new ExperimentStep
        {
            Id = Guid.CreateVersion7(), ExperimentId = experiment.Id, Ordinal = experiment.Steps.Count + 1, Kind = "refill",
            CupsJson = JsonSerializer.Serialize(new[] { new StepCup { Number = cupNumber, FreshDry = request.DryGrams, FreshWater = request.WaterGrams, Adds = adds, WetAfter = cup.Wet } }, Json),
            ActorUserId = userId, StartedAt = DateTimeOffset.UtcNow, DippedAt = DateTimeOffset.UtcNow
        });
        experiment.CupsJson = JsonSerializer.Serialize(cups, Json);
        experiment.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return await GetAsync(id, null, cancellationToken);
    }

    // ---- reading ----

    /// <summary>One experiment as the screens show it. viewer: the participant's own only (404 otherwise).</summary>
    public async Task<ExperimentDto> GetAsync(Guid id, Guid? viewer, CancellationToken cancellationToken)
    {
        var e = await db.Experiments.AsNoTracking().Include(x => x.BaseGlaze).Include(x => x.Participant).Include(x => x.Steps)
            .SingleOrDefaultAsync(x => x.Id == id && (viewer == null || (x.ParticipantUserId == viewer && x.DeletedAt == null)), cancellationToken) ?? throw NotFound();
        var stages = StagesOf(e);
        var cups = CupsOf(e);
        var plan = ExperimentMath.Plan(stages);
        var steps = e.Steps.OrderBy(s => s.Ordinal).ToList();
        var mixes = steps.Where(s => s.Kind == "mix").ToList();
        var done = mixes.Where(s => s.DippedAt != null).ToList();
        var actorIds = steps.Select(s => s.ActorUserId).Distinct().ToList();
        var actors = await db.Users.AsNoTracking().Where(u => actorIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.Username, cancellationToken);

        var stageDtos = stages.Select((s, i) => new ExperimentStageDto(i, i + 1, s.Material, s.Mode, s.Start, s.Step, 1,
            s.Constant, Summary(s, e.CupCount), 1, done.Count(d => d.StageIndex == i), mixes.All(m => m.StageIndex != i))).ToList();
        var cupDtos = cups.Select(c => new ExperimentCupDto(c.Number, Grams(c.Dry), Grams(c.Water), Grams(c.Wet),
            stages.Select(s => s.Material).Where(c.Additives.ContainsKey).Select(m => new CupMaterialDto(m, Grams(c.Additives[m]), ExperimentMath.Round(c.Percentages()[m]))).ToList(), c.NeedsGlaze)).ToList();

        ExperimentStepDto? current = null;
        if (mixes.FirstOrDefault(s => s.DippedAt is null) is { } open) current = StepDto(open, stages, actors);
        else if (NextStep(e, stages) is { } next)
        {
            // A preview of the next step from the cups as they are now.
            var preview = cups.Select(c =>
            {
                if (c.NeedsGlaze) return new StepCupDto(c.Number, null, null, null, null, null, null, null, true);
                var target = ExperimentMath.Target(stages, next, c.Number);
                var add = ExperimentMath.GramsToAdd(c, next.Material, target);
                return new StepCupDto(c.Number, target, Grams(add, 3), Grams(c.Wet + add), null, null, null, null, false);
            }).ToList();
            var stage = stages[next.StageIndex];
            current = new ExperimentStepDto(null, "mix", next.StageIndex, next.StageIndex + 1, next.Level, 1, next.Material, stage.Mode, preview, null, null, null, null, null, null);
        }

        var history = steps.Where(s => s.DippedAt != null).Select(s => StepDto(s, stages, actors)).ToList();
        var recipes = mixes.Sum(s => (JsonSerializer.Deserialize<List<StepCup>>(s.CupsJson, Json) ?? []).Count(c => c.RecipeId != null));
        var tiles = done.Sum(s => (JsonSerializer.Deserialize<List<StepCup>>(s.CupsJson, Json) ?? []).Count(c => !c.Skipped)) * e.TilesPerStep;
        var finished = mixes.All(m => m.DippedAt != null) && NextStep(e, stages) is null;
        return new ExperimentDto(e.Id, e.Name, e.BaseGlaze.Code, e.BaseGlaze.Name, e.ParticipantUserId, e.Participant.Username,
            string.IsNullOrWhiteSpace(e.Participant.DisplayName) ? null : e.Participant.DisplayName, e.Participant.ParticipantCode, e.CupCount, e.DryGrams, e.WaterGrams,
            e.TilesPerStep, JsonSerializer.Deserialize<List<string>>(e.ClayBodiesJson, Json) ?? [], e.MinReserveGrams, stageDtos, cupDtos, current, history,
            new ExperimentProgressDto(done.Count, plan.Count, recipes, tiles), finished, e.CreatedAt, e.UpdatedAt, e.DeletedAt);
    }

    private static ExperimentStepDto StepDto(ExperimentStep s, IReadOnlyList<ExperimentStage> stages, IReadOnlyDictionary<Guid, string> actors)
    {
        var cups = (JsonSerializer.Deserialize<List<StepCup>>(s.CupsJson, Json) ?? []).Select(c => new StepCupDto(c.Number, c.Target, c.Add is { } a ? Grams(a, 3) : null,
            c.WetBefore is { } b ? Grams(b) : null, c.WetAfter is { } w ? Grams(w) : null, c.RecipeId, c.RecipeCode, c.RecipeName, c.Skipped,
            c.FreshDry, c.FreshWater, c.Adds?.Select(x => new CupMaterialDto(x.Key, Grams(x.Value, 3), 0)).ToList())).ToList();
        var stage = s.StageIndex is { } i && i < stages.Count ? stages[i] : null;
        return new ExperimentStepDto(s.Id, s.Kind, s.StageIndex, s.StageIndex + 1, s.Level, stage is null ? null : 1,
            s.Material, stage?.Mode, cups, s.StartedAt, s.DippedAt, s.WeighedCup,
            s.WeighedGrams, s.Retention, actors.GetValueOrDefault(s.ActorUserId));
    }

    // ---- helpers ----

    /// <summary>The first step in the plan not yet started.</summary>
    private static PlannedStep? NextStep(Experiment e, IReadOnlyList<ExperimentStage> stages)
    {
        var mixes = e.Steps.Where(s => s.Kind == "mix").ToList();
        return ExperimentMath.Plan(stages).FirstOrDefault(p => !mixes.Any(m => m.StageIndex == p.StageIndex && m.Level == p.Level));
    }

    private static string Summary(ExperimentStage s, int cups) =>
        !s.Variable ? $"{Percent(s.Constant)}% in every cup"
        : cups == 1 || s.Step == 0 ? $"{Percent(s.Start)}% in {(cups == 1 ? "cup 1" : "every cup")}"
        : $"{Percent(s.Start)}% in cup 1 {(s.Step < 0 ? "down" : "up")} to {Percent(s.Start + s.Step * (cups - 1))}% in cup {cups}";

    /// <summary>Every cup's amount in every series is between 0 and 100% (a series going down must not pass 0).</summary>
    private static void CheckAmounts(IReadOnlyList<ExperimentStage> stages, int cups)
    {
        for (var i = 0; i < stages.Count; i++)
        {
            var s = stages[i];
            if (!s.Variable) continue;
            var last = s.Start + s.Step * (cups - 1);
            if (last < -1e-9 || last > 100 + 1e-9) throw ApiProblems.Validation($"stages[{i}]", $"Stage {i + 1}: with {cups} cups the amounts run from {Percent(s.Start)}% to {Percent(last)}%; keep them between 0 and 100%.");
        }
    }

    private static string Percent(double value) => ExperimentMath.Round(value).ToString("0.####", CultureInfo.InvariantCulture);
    private static double Grams(double value, int decimals = 2) => Math.Round(value, decimals, MidpointRounding.AwayFromZero);

    private static bool SameExceptLevels(ExperimentStage a, ExperimentStage b) =>
        string.Equals(a.Material, b.Material, StringComparison.OrdinalIgnoreCase) && a.Mode == b.Mode && a.Start == b.Start && a.Step == b.Step && a.Constant == b.Constant;

    private static List<ExperimentStage> StagesOf(Experiment e) => JsonSerializer.Deserialize<List<ExperimentStage>>(e.StagesJson, Json) ?? [];

    private static List<ExperimentCup> CupsOf(Experiment e) =>
        (JsonSerializer.Deserialize<List<ExperimentCup>>(e.CupsJson, Json) ?? []).Select(c => { c.Additives = new Dictionary<string, double>(c.Additives, StringComparer.OrdinalIgnoreCase); return c; }).ToList();

    private async Task<Experiment> FindAsync(Guid id, Guid? participant, CancellationToken cancellationToken) =>
        await db.Experiments.Include(x => x.BaseGlaze).Include(x => x.Steps)
            .SingleOrDefaultAsync(x => x.Id == id && x.DeletedAt == null && (participant == null || x.ParticipantUserId == participant), cancellationToken) ?? throw NotFound();

    private async Task<BaseGlaze> BaseAsync(string? code, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(code)) throw ApiProblems.Validation("baseCode", "Choose the base glaze the cups are made of.");
        var normalized = code.Trim().ToUpperInvariant();
        return await db.BaseGlazes.SingleOrDefaultAsync(x => x.Code == normalized && x.DeletedAt == null, cancellationToken)
            ?? throw new ApiProblemException(404, "base_not_found", "Not found", $"Base {code} does not exist.");
    }

    private async Task<AppUser> ParticipantAsync(Guid userId, CancellationToken cancellationToken)
    {
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == userId, cancellationToken)
                   ?? throw ApiProblems.Validation("participantUserId", "That participant does not exist.");
        if (user.Role != UserRole.User) throw ApiProblems.Validation("participantUserId", "Experiments go to participants, not administrators.");
        if (user.Status != AccountStatus.Active) throw ApiProblems.Validation("participantUserId", $"@{user.Username}'s account is not active.");
        return user;
    }

    private static List<ExperimentStage> Stages(IReadOnlyList<ExperimentStageInput>? input)
    {
        if (input is null || input.Count == 0) throw ApiProblems.Validation("stages", "Add at least one stage: the material and how much of it.");
        if (input.Count > MaxStages) throw ApiProblems.Validation("stages", $"Use at most {MaxStages} stages.");
        var stages = new List<ExperimentStage>();
        for (var i = 0; i < input.Count; i++)
        {
            var s = input[i];
            var field = $"stages[{i}]";
            var material = s.Material?.Trim();
            if (string.IsNullOrEmpty(material) || material.Length > 255) throw ApiProblems.Validation(field, $"Stage {i + 1}: choose the material.");
            if (stages.Any(x => string.Equals(x.Material, material, StringComparison.OrdinalIgnoreCase)))
                throw ApiProblems.Validation(field, $"{material} is in two stages. Give each stage its own material.");
            var mode = s.Mode?.Trim().ToLowerInvariant() ?? ExperimentMath.Variable;
            if (mode is not (ExperimentMath.Variable or ExperimentMath.Constant)) throw ApiProblems.Validation(field, $"Stage {i + 1}: mode is variable or constant.");
            var start = s.Start ?? 0;
            var step = s.Step ?? 0;
            var constant = s.Constant ?? 0;
            // A series can go down as well as up (100 to 1, meeting 2026-11-10); every cup's amount is checked once the cups are known.
            if (start < 0 || constant < 0 || start > 100 || Math.Abs(step) > 100 || constant > 100) throw ApiProblems.Validation(field, $"Stage {i + 1}: amounts are between 0 and 100%.");
            if (mode == ExperimentMath.Variable)
            {
                if (start <= 0 && start + step <= 0) throw ApiProblems.Validation(field, $"Stage {i + 1}: give an amount above 0.");
            }
            else if (constant <= 0) throw ApiProblems.Validation(field, $"Stage {i + 1}: give the amount in every cup, above 0.");
            // The step keeps its decimals: 0.2 to 3 split into 10 places is a step of 0.3111…, and cup 10 must read 3.
            stages.Add(new ExperimentStage(material, mode, ExperimentMath.Round(start), Math.Round(step, 10), 1, ExperimentMath.Round(constant)));
        }
        return stages;
    }

    private List<string> ClayBodies(IReadOnlyList<string>? requested)
    {
        var options = ingestion.Value;
        if (requested is null || requested.Count == 0) return [.. options.AllowedClayBodies];
        var result = new List<string>();
        foreach (var value in requested)
        {
            if (!options.TryNormalizeClayBody(value, out var clayBody) || clayBody is null)
                throw ApiProblems.Validation("clayBodies", $"Clay bodies must be among {string.Join(", ", options.AllowedClayBodies)}.");
            result.Add(clayBody);
        }
        return result;
    }

    private static int TilesPerStep(int? value) => value is null ? 3 : value is >= 1 and <= 12 ? value.Value : throw ApiProblems.Validation("tilesPerStep", "Dip 1 to 12 tiles after each change.");

    private static double Reserve(double? value) => value is null ? 10 : value is >= 0 and <= 10_000 ? value.Value : throw ApiProblems.Validation("minReserveGrams", "Give the reserve in grams, 0 or more.");

    private static ApiProblemException NotFound() => new(404, "experiment_not_found", "Not found", "No experiment with this ID.");
}
