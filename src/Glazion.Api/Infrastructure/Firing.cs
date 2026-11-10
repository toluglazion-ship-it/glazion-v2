using Glazion.Api.Contracts;

namespace Glazion.Api.Infrastructure;

/// <summary>How a tile is fired, as a recipe or base records it: the cone ("6", "06" for cone 06) and the atmosphere.</summary>
public sealed record FiringInput(string? Cone, string? Atmosphere);

/// <summary>
/// Cone and atmosphere on bases and recipes (meeting 2026-10-29: every participant sees them on the tile they make).
/// A base sets them; a variation inherits them unless it sets its own. Null leaves a value as it is on an edit; an empty
/// string clears it.
/// </summary>
public static class Firing
{
    public static readonly string[] Atmospheres = ["oxidation", "reduction", "neutral"];

    /// <summary>"Cone 6", "∆6", "^6" and "6" are cone 6; "06" is cone 06. Null for nothing; refused when it isn't a cone.</summary>
    public static string? NormalizeCone(string? value, string field = "cone")
    {
        if (value is null) return null;
        var text = value.Trim();
        foreach (var prefix in new[] { "cone", "∆", "Δ", "^" })
            if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) text = text[prefix.Length..].Trim();
        if (text.Length == 0) return "";
        if (AdminImageFilter.ParseCone(text) is null) throw ApiProblems.Validation(field, "Give the cone as a number, such as 6, 10 or 06.");
        return text;
    }

    public static string? NormalizeAtmosphere(string? value, string field = "atmosphere")
    {
        if (value is null) return null;
        var text = value.Trim().ToLowerInvariant();
        if (text.Length == 0) return "";
        if (!Atmospheres.Contains(text)) throw ApiProblems.Validation(field, $"The atmosphere is one of {string.Join(", ", Atmospheres)}.");
        return text;
    }

    /// <summary>An edit's value: unchanged when null, cleared when empty.</summary>
    public static string? Apply(string? current, string? normalized) => normalized is null ? current : normalized.Length == 0 ? null : normalized;

    /// <summary>The cone as the photos store it (cone 06 is -6).</summary>
    public static decimal? ConeNumber(string? cone) => string.IsNullOrWhiteSpace(cone) ? null : AdminImageFilter.ParseCone(cone);
}
