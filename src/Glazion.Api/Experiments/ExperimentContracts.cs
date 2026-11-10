namespace Glazion.Api.Experiments;

// Shapes match @glazion/types (Experiment*). docs/features/cup-experiments.md.

/// <summary>
/// One stage as set up: material, mode ("variable" = Series or "constant" = Fixed), and its amounts in % of the dry
/// base. Every stage is one round across the cups: a Series gives cup n start + step × (n − 1) %, a Fixed one the
/// constant % in every cup. levels is ignored (older data).
/// </summary>
public sealed record ExperimentStageInput(string? Material, string? Mode, double? Start, double? Step, int? Levels, double? Constant);

/// <summary>
/// A new experiment for one participant on one base. cups: 1–20 (10 by default). dryGrams/waterGrams per cup (100 g and
/// 60 g). tilesPerStep: dipped after every change (3). clayBodies: the clay bodies of those tiles, one tile task per
/// distinct one (all three by default; one clay body means every tile is that clay). minReserveGrams: a cup asks for
/// fresh glaze when the next dip would leave less (10 g). stages: 1–5.
/// </summary>
public sealed record CreateExperimentRequest(string? Name, string? BaseCode, Guid ParticipantUserId, int? Cups, double? DryGrams, double? WaterGrams,
    int? TilesPerStep, IReadOnlyList<string>? ClayBodies, double? MinReserveGrams, IReadOnlyList<ExperimentStageInput>? Stages);

/// <summary>
/// Only what is sent changes. stages replaces the list; a stage whose first step was started can't change (only gain
/// levels). cups, dryGrams, waterGrams and participantUserId: only before the first step (the cups are mixed then).
/// </summary>
public sealed record UpdateExperimentRequest(string? Name, IReadOnlyList<ExperimentStageInput>? Stages, int? TilesPerStep, IReadOnlyList<string>? ClayBodies, double? MinReserveGrams,
    int? Cups = null, double? DryGrams = null, double? WaterGrams = null, Guid? ParticipantUserId = null);

/// <summary>
/// The cup experiment registered with a base (the Register base form): each colorant row is a stage. Stage 1 sets the
/// cups (cups: one per amount, 1–20). The rest as in CreateExperimentRequest; name defaults to the base's name.
/// </summary>
public sealed record BaseExperimentRequest(Guid ParticipantUserId, int? Cups, double? DryGrams, double? WaterGrams, int? TilesPerStep, IReadOnlyList<string>? ClayBodies,
    double? MinReserveGrams, IReadOnlyList<ExperimentStageInput>? Stages, string? Name = null);

/// <summary>
/// The stage is done and its tiles dipped. Optionally which cup was weighed and how much wet glaze is left in it, in grams
/// (the cup's own weight taken off); without it, a placeholder amount per tile is taken out of every cup.
/// </summary>
public sealed record DippedRequest(int? CupNumber = null, double? WetGrams = null);

/// <summary>Fresh glaze poured into a cup that ran low: its dry base and water, in grams.</summary>
public sealed record RefillRequest(double DryGrams, double WaterGrams);

public sealed record ExperimentStageDto(int Index, int Number, string Material, string Mode, double Start, double Step, int Levels, double Constant,
    string Summary, int StepsTotal, int StepsDone, bool Editable);

/// <summary>A material in a cup: grams now, and its % of the dry base (the recipe in the cup).</summary>
public sealed record CupMaterialDto(string Material, double Grams, double Percent);

public sealed record ExperimentCupDto(int Number, double DryGrams, double WaterGrams, double WetGrams, IReadOnlyList<CupMaterialDto> Materials, bool NeedsGlaze);

/// <summary>
/// One cup in a step. Mix: the % it should hold (targetPercent), the grams to add, the wet glaze before dipping, and the
/// recipe its tiles are (code written on the back). skipped: the cup was too low and left out. Refill: the fresh glaze
/// and the grams of each earlier material added to it.
/// </summary>
public sealed record StepCupDto(int Number, double? TargetPercent, double? AddGrams, double? WetBeforeGrams, double? WetAfterGrams, Guid? RecipeId, string? RecipeCode,
    string? RecipeName, bool Skipped, double? FreshDryGrams = null, double? FreshWaterGrams = null, IReadOnlyList<CupMaterialDto>? RefillAdds = null);

/// <summary>
/// A step. stageNumber/level/levels: "Stage 3, level 2 of 4". started: materials are being added (codes given);
/// dippedAt: tiles dipped and a cup weighed. Without an id it is the next step, not started yet (a preview).
/// </summary>
public sealed record ExperimentStepDto(Guid? Id, string Kind, int? StageIndex, int? StageNumber, int? Level, int? Levels, string? Material, string? Mode,
    IReadOnlyList<StepCupDto> Cups, DateTimeOffset? StartedAt, DateTimeOffset? DippedAt, int? WeighedCup, double? WeighedGrams, double? Retention, string? ActorUsername);

public sealed record ExperimentProgressDto(int StepsDone, int StepsTotal, int Recipes, int Tiles);

/// <summary>
/// An experiment with where it has got to. current: the open step (started, not yet dipped) or, when none is open, the
/// next one as a preview; null when every step is done. history: every step done, newest last.
/// </summary>
public sealed record ExperimentDto(Guid Id, string Name, string BaseCode, string? BaseName, Guid ParticipantUserId, string ParticipantUsername, string? ParticipantDisplayName,
    string? ParticipantCode, int CupCount, double DryGrams, double WaterGrams, int TilesPerStep, IReadOnlyList<string> ClayBodies, double MinReserveGrams,
    IReadOnlyList<ExperimentStageDto> Stages, IReadOnlyList<ExperimentCupDto> Cups, ExperimentStepDto? Current, IReadOnlyList<ExperimentStepDto> History,
    ExperimentProgressDto Progress, bool Finished, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? DeletedAt);
