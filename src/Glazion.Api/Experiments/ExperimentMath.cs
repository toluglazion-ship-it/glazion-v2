namespace Glazion.Api.Experiments;

/// <summary>
/// One stage of a cup experiment (meeting 2026-11-09): one round across every cup, then dip. Series (variable): cup n
/// gets Start + Step × (n − 1) %, so 0.5 to 5 split into 10 places is 0.5, 1 … 5. Fixed (constant): Constant % in every
/// cup. Each stage adds Material on top of everything added before. Levels is kept for older data and is always 1 now.
/// </summary>
public sealed record ExperimentStage(string Material, string Mode, double Start, double Step, int Levels, double Constant)
{
    public bool Variable => Mode == ExperimentMath.Variable;
}

/// <summary>What is in a cup now, in grams: dry base, water and each added material. NeedsGlaze: too little left for another dip.</summary>
public sealed class ExperimentCup
{
    public int Number { get; set; }
    public double Dry { get; set; }
    public double Water { get; set; }
    public Dictionary<string, double> Additives { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public bool NeedsGlaze { get; set; }

    public double Wet => Dry + Water + Additives.Values.Sum();

    /// <summary>Each material as a % of the dry base still in the cup: what the glaze in it is now.</summary>
    public Dictionary<string, double> Percentages() => Additives.ToDictionary(a => a.Key, a => Dry > 0 ? a.Value / Dry * 100 : 0, StringComparer.OrdinalIgnoreCase);
}

/// <summary>One step in order: which stage, which level of it (1-based), and the material it adds.</summary>
public sealed record PlannedStep(int StageIndex, int Level, string Material);

/// <summary>
/// The arithmetic of a cup experiment (Glazion Studio spec, "Sequential wet-slurry research matrix"). Amounts are a % of
/// the dry base glaze still in the cup. Dipping takes glaze, water and every material out in the same proportion
/// (the slurry is mixed), so the percentages already in a cup never change; only the grams to add for the next step do.
/// The participant weighs one cup after dipping and that cup's loss is applied to every cup (they are dipped alike).
/// </summary>
public static class ExperimentMath
{
    public const string Variable = "variable";
    public const string Constant = "constant";

    /// <summary>The steps in order: one per stage, every cup at once.</summary>
    public static IReadOnlyList<PlannedStep> Plan(IReadOnlyList<ExperimentStage> stages) =>
        stages.Select((s, i) => new PlannedStep(i, 1, s.Material)).ToList();

    /// <summary>The % of the stage's material a cup should hold after this step: its own place in a series, or the fixed amount.</summary>
    public static double Target(IReadOnlyList<ExperimentStage> stages, PlannedStep step, int cupNumber)
    {
        var stage = stages[step.StageIndex];
        return Round(stage.Variable ? stage.Start + stage.Step * (cupNumber - 1) : stage.Constant);
    }

    /// <summary>
    /// Grams of the material to add so the cup holds `target` % of its remaining dry base. Never negative: a material
    /// can't be taken out again, so a lower target adds nothing (and the cup keeps what it has).
    /// </summary>
    public static double GramsToAdd(ExperimentCup cup, string material, double target)
    {
        var wanted = cup.Dry * target / 100;
        return Math.Max(0, wanted - cup.Additives.GetValueOrDefault(material));
    }

    /// <summary>Adds the grams to the cup (the participant has put them in and mixed).</summary>
    public static void Add(ExperimentCup cup, string material, double grams) =>
        cup.Additives[material] = cup.Additives.GetValueOrDefault(material) + grams;

    /// <summary>
    /// After dipping: the weighed cup held `wetBefore` and now holds `weighed`, so the share left is weighed / wetBefore.
    /// Every cup keeps that share of its dry base, water and each material.
    /// </summary>
    public static double Retention(double wetBefore, double weighed) => wetBefore > 0 ? weighed / wetBefore : 0;

    public static void Keep(ExperimentCup cup, double retention)
    {
        cup.Dry *= retention;
        cup.Water *= retention;
        foreach (var key in cup.Additives.Keys.ToList()) cup.Additives[key] *= retention;
    }

    /// <summary>
    /// Fresh glaze added to a cup that ran low: each material it already holds, in grams, to add to the fresh glaze so
    /// the cup stays the same recipe (the same % of the fresh dry base).
    /// </summary>
    public static Dictionary<string, double> RefillAdds(ExperimentCup cup, double freshDry) =>
        cup.Percentages().ToDictionary(p => p.Key, p => freshDry * p.Value / 100, StringComparer.OrdinalIgnoreCase);

    /// <summary>Percentages kept to 4 decimals (what a recipe stores), so 2% of a weighed cup reads 2, not 1.9999999.</summary>
    public static double Round(double value) => Math.Round(value, 4, MidpointRounding.AwayFromZero);
}
