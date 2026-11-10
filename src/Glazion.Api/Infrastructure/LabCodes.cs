using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Glazion.Api.Data;
using Glazion.Api.Domain;
using Glazion.Api.Tasks;
using Microsoft.EntityFrameworkCore;

namespace Glazion.Api.Infrastructure;

/// <summary>
/// The lab code system: {Group}{Participant}-B{Base}-{Recipe:000}, e.g. A03-B2-009.
/// A03 = group A, participant 03; B2 = that participant's second registered base glaze; 009 = ninth variation under it.
/// Up to 999 bases per participant and 2,000 variations per base (meeting 2026-10-29): variation numbers keep three
/// digits up to 999, so codes already written on tiles never change, and run on as A03-B2-1000 … A03-B2-2000.
/// The code identifies a record; the database holds the complete formulation.
/// </summary>
public static partial class LabCode
{
    public const int MaxRecipeNumber = 2000;
    public const int MaxBaseNumber = 999;

    [GeneratedRegex("^[A-Z][0-9]{2}$")] private static partial Regex ParticipantPattern();
    [GeneratedRegex("^([A-Z][0-9]{2})-B([1-9][0-9]{0,2})$")] private static partial Regex BasePattern();
    [GeneratedRegex("^([A-Z][0-9]{2})-B([1-9][0-9]{0,2})-([0-9]{3}|[1-9][0-9]{3})$")] private static partial Regex RecipePattern();

    public static bool IsValidParticipant(string? code) => code is not null && ParticipantPattern().IsMatch(code);
    public static string NormalizeParticipant(string code) => code.Trim().ToUpperInvariant();

    public static string Base(string participant, int baseNumber)
    {
        if (!IsValidParticipant(participant)) throw new ArgumentException("Participant code must be a letter followed by two digits, e.g. A03.", nameof(participant));
        if (baseNumber is < 1 or > MaxBaseNumber) throw new ArgumentOutOfRangeException(nameof(baseNumber), "Base numbers run from 1 to 999.");
        return $"{participant}-B{baseNumber.ToString(CultureInfo.InvariantCulture)}";
    }

    public static string Recipe(string participant, int baseNumber, int recipeNumber)
    {
        if (recipeNumber is < 1 or > MaxRecipeNumber) throw new ArgumentOutOfRangeException(nameof(recipeNumber), "Recipe numbers run from 001 to 2000.");
        return $"{Base(participant, baseNumber)}-{recipeNumber.ToString("000", CultureInfo.InvariantCulture)}";
    }

    public static bool TryParseBase(string? code, out string participant, out int baseNumber)
    {
        participant = string.Empty; baseNumber = 0;
        if (code is null) return false;
        var match = BasePattern().Match(code.Trim().ToUpperInvariant());
        if (!match.Success) return false;
        participant = match.Groups[1].Value;
        baseNumber = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        return true;
    }

    public static bool TryParseRecipe(string? code, out string participant, out int baseNumber, out int recipeNumber)
    {
        participant = string.Empty; baseNumber = 0; recipeNumber = 0;
        if (code is null) return false;
        var match = RecipePattern().Match(code.Trim().ToUpperInvariant());
        if (!match.Success) return false;
        participant = match.Groups[1].Value;
        baseNumber = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        recipeNumber = int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
        return recipeNumber is >= 1 and <= MaxRecipeNumber;
    }

    /// <summary>Canonical upper-case form of a recipe code, or null when the input is not a recipe code.</summary>
    public static string? NormalizeRecipe(string? code) => TryParseRecipe(code, out var p, out var b, out var r) ? Recipe(p, b, r) : null;
}

public sealed record MaterialRow(string RawName, decimal Amount, string Unit);

public sealed record VariationInput(string Name, string? Description, IReadOnlyList<MaterialRow>? Additions, FiringInput? Firing = null);

/// <summary>Only the fields sent change. formulation replaces the whole base formulation.</summary>
public sealed record BaseGlazeEdit(string? Name, string? Description, IReadOnlyList<MaterialRow>? Formulation, string? ChangeNote, FiringInput? Firing = null);

public sealed class LabCodeException(int statusCode, string code, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;
}

/// <summary>
/// Issues codes and enforces the register rules: numbers come from a monotonically increasing sequence per owner
/// (bases) and per base (recipes) and are never reused; formulations are append-only; an attempt to register a
/// number twice is rejected by the unique indexes and retried once with the next number.
/// </summary>
public sealed class LabCodeService(GlazionDbContext db)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> Units = new(StringComparer.OrdinalIgnoreCase) { "%", "g", "kg", "oz", "lb" };

    /// <summary>
    /// The next free participant code, for a user created without one (meeting 2026-11-09): P01, P02 … P99, then Q01 and
    /// so on. Codes typed by hand in other letters are left alone.
    /// </summary>
    public async Task<string> NextParticipantCodeAsync(CancellationToken cancellationToken)
    {
        var taken = (await db.Users.AsNoTracking().Where(x => x.ParticipantCode != null).Select(x => x.ParticipantCode!).ToListAsync(cancellationToken)).ToHashSet();
        for (var letter = 'P'; letter <= 'Z'; letter++)
        {
            var highest = taken.Where(c => c[0] == letter && LabCode.IsValidParticipant(c)).Select(c => int.Parse(c[1..], System.Globalization.CultureInfo.InvariantCulture)).DefaultIfEmpty(0).Max();
            for (var n = highest + 1; n <= 99; n++)
            {
                var code = $"{letter}{n:00}";
                if (!taken.Contains(code)) return code;
            }
        }
        throw new LabCodeException(409, "participant_codes_full", "Every participant code from P01 to Z99 is in use. Type one by hand.");
    }

    public async Task AssignParticipantCodeAsync(AppUser user, string code, CancellationToken cancellationToken)
    {
        var normalized = LabCode.NormalizeParticipant(code);
        if (!LabCode.IsValidParticipant(normalized)) throw new LabCodeException(400, "invalid_participant_code", "Participant code must be a group letter followed by two digits, e.g. A03.");
        if (user.ParticipantCode == normalized) return;
        if (user.ParticipantCode is not null && await db.BaseGlazes.AnyAsync(x => x.OwnerUserId == user.Id, cancellationToken))
            throw new LabCodeException(409, "participant_code_in_use", "This participant already has registered bases; the participant code cannot change because codes written on tiles would stop matching.");
        if (await db.Users.AnyAsync(x => x.ParticipantCode == normalized && x.Id != user.Id, cancellationToken))
            throw new LabCodeException(409, "participant_code_taken", $"Participant code {normalized} is already assigned.");
        user.ParticipantCode = normalized;
        user.UpdatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>firing: how its tiles are fired. A derived base without one takes its parent's.</summary>
    public async Task<BaseGlaze> RegisterBaseAsync(AppUser owner, string name, string? description, IReadOnlyList<MaterialRow> formulation, string? derivedFromBaseCode, string? changeNote, Guid actorId, CancellationToken cancellationToken,
        FiringInput? firing = null)
    {
        var cone = Firing.Apply(null, Firing.NormalizeCone(firing?.Cone));
        var atmosphere = Firing.Apply(null, Firing.NormalizeAtmosphere(firing?.Atmosphere));
        if (owner.ParticipantCode is null) throw new LabCodeException(409, "participant_code_missing", "Assign a participant code (e.g. A03) before registering base glazes.");
        if (string.IsNullOrWhiteSpace(name) || name.Length > 180) throw new LabCodeException(400, "invalid_name", "Base name is required (max 180 characters).");
        ValidateRows(formulation, "formulation", requireAny: true);

        BaseGlaze? parent = null;
        if (!string.IsNullOrWhiteSpace(derivedFromBaseCode))
        {
            parent = await db.BaseGlazes.SingleOrDefaultAsync(x => x.Code == derivedFromBaseCode.Trim().ToUpperInvariant(), cancellationToken)
                     ?? throw new LabCodeException(404, "base_not_found", $"Base {derivedFromBaseCode} does not exist.");
            if (string.IsNullOrWhiteSpace(changeNote)) throw new LabCodeException(400, "change_note_required", "State what changed relative to the base this one is derived from, e.g. \"Original base + 2% rutile\".");
        }
        return await CreateBaseAsync(owner, name, description, formulation, parent, changeNote, null, cone ?? parent?.Cone, atmosphere ?? parent?.Atmosphere, actorId, cancellationToken);
    }

    /// <summary>
    /// A tester liked a variation and builds on it: its base formulation plus its additions become the constant of a
    /// new base in <paramref name="owner"/>'s register (A03-B1-004 → A03-B5), derived from the variation's base. The
    /// change note records where it came from. Each variation is promoted once; asking again is a 409 naming the base.
    /// </summary>
    public async Task<BaseGlaze> PromoteVariationAsync(Recipe variation, AppUser owner, string? name, string? description, Guid actorId, CancellationToken cancellationToken)
    {
        if (variation.BaseGlazeId is null || variation.Code is null)
            throw new LabCodeException(409, "not_a_variation", "Only a coded variation (e.g. A03-B1-004) can become a base.");
        if (!variation.IsActive) throw new LabCodeException(409, "variation_inactive", $"{variation.Code} is inactive; reactivate it before building on it.");
        var existing = await db.BaseGlazes.AsNoTracking().Where(x => x.PromotedFromRecipeId == variation.Id).Select(x => x.Code).FirstOrDefaultAsync(cancellationToken);
        if (existing is not null) throw new LabCodeException(409, "already_promoted", $"{variation.Code} is already base {existing}.");

        var parent = variation.BaseGlaze ?? await db.BaseGlazes.SingleAsync(x => x.Id == variation.BaseGlazeId, cancellationToken);
        var baseRows = JsonSerializer.Deserialize<List<MaterialRow>>(parent.FormulationJson, Json) ?? [];
        var additions = JsonSerializer.Deserialize<List<MaterialRow>>(variation.AdditionsJson, Json) ?? [];
        var changeNote = additions.Count == 0
            ? $"Promoted from {variation.Code} ({parent.Code} unchanged)"
            : $"Promoted from {variation.Code}: {parent.Code} + {Describe(additions)}";
        if (changeNote.Length > 500) changeNote = changeNote[..497] + "...";
        return await CreateBaseAsync(owner, string.IsNullOrWhiteSpace(name) ? variation.Name : name, description ?? variation.Description,
            Combine(baseRows, additions), parent, changeNote, variation.Id, variation.Cone ?? parent.Cone, variation.Atmosphere ?? parent.Atmosphere, actorId, cancellationToken);
    }

    /// <summary>"2.5% Shell Pink 6000, 1% Iron Oxide".</summary>
    public static string Describe(IEnumerable<MaterialRow> rows) =>
        string.Join(", ", rows.Select(r => $"{r.Amount.ToString("0.###", CultureInfo.InvariantCulture)}{(r.Unit == "%" ? "%" : " " + r.Unit)} {r.RawName}"));

    private async Task<BaseGlaze> CreateBaseAsync(AppUser owner, string name, string? description, IReadOnlyList<MaterialRow> formulation, BaseGlaze? parent, string? changeNote, Guid? promotedFromRecipeId,
        string? cone, string? atmosphere, Guid actorId, CancellationToken cancellationToken)
    {
        if (owner.ParticipantCode is null) throw new LabCodeException(409, "participant_code_missing", "Assign a participant code (e.g. A03) before registering base glazes.");
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 180) throw new LabCodeException(400, "invalid_name", "Base name is required (max 180 characters).");
        ValidateRows(formulation, "formulation", requireAny: true);

        for (var attempt = 1; ; attempt++)
        {
            var next = (await db.BaseGlazes.Where(x => x.OwnerUserId == owner.Id).MaxAsync(x => (int?)x.BaseNumber, cancellationToken) ?? 0) + 1;
            if (next > LabCode.MaxBaseNumber)
                throw new LabCodeException(409, "participant_full", $"{owner.ParticipantCode} already has {LabCode.MaxBaseNumber} base glazes, the most a participant can have.");
            var entity = new BaseGlaze
            {
                Id = Guid.CreateVersion7(),
                OwnerUserId = owner.Id,
                ParticipantCode = owner.ParticipantCode,
                BaseNumber = next,
                Code = LabCode.Base(owner.ParticipantCode, next),
                Name = name.Trim(),
                Description = description?.Trim() ?? string.Empty,
                FormulationJson = JsonSerializer.Serialize(Normalize(formulation), Json),
                DerivedFromBaseId = parent?.Id,
                ChangeNote = changeNote?.Trim(),
                PromotedFromRecipeId = promotedFromRecipeId,
                Cone = cone,
                Atmosphere = atmosphere,
                CreatedBy = actorId
            };
            db.BaseGlazes.Add(entity);
            try { await db.SaveChangesAsync(cancellationToken); return entity; }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex) && attempt < 3)
            {
                db.Entry(entity).State = EntityState.Detached; // another request took this number; take the next one
            }
        }
    }

    /// <summary>
    /// Corrects a base. Name, description and change note can always change. A new formulation is accepted only while
    /// none of its variations is in use (started by a participant, made, photographed or in the library): every variation's
    /// stored rows are then rebuilt as the new base plus its own additions. Once one is in use the formulation stays, so
    /// nobody's tile changes under them, and a changed base is registered as a new base derived from this one (meeting
    /// 2026-10-29). Returns the variations whose rows were rebuilt.
    /// </summary>
    /// <summary>
    /// Gives a base to another participant (meeting 2026-11-10): it takes their next base number, so P06-B2 can become
    /// P07-B1, and its cup experiment goes to them. Only before stage 1 starts: once it has, the cups are mixed and the
    /// recipes carry the code, so 409 base_started.
    /// </summary>
    public async Task MoveBaseAsync(BaseGlaze baseGlaze, string participantCode, CancellationToken cancellationToken)
    {
        var code = participantCode.Trim().ToUpperInvariant();
        if (code == baseGlaze.ParticipantCode) return;
        var owner = await db.Users.SingleOrDefaultAsync(u => u.ParticipantCode == code, cancellationToken)
            ?? throw new LabCodeException(400, "participant_not_found", $"No participant has the code {code}.");
        if (await db.Recipes.AnyAsync(r => r.BaseGlazeId == baseGlaze.Id, cancellationToken)
            || await db.ExperimentSteps.AnyAsync(s => db.Experiments.Any(e => e.Id == s.ExperimentId && e.BaseGlazeId == baseGlaze.Id), cancellationToken))
            throw new LabCodeException(409, "base_started", $"Stage 1 of {baseGlaze.Code} has started, so its recipes carry the code and it stays with {baseGlaze.ParticipantCode}.");
        var next = (await db.BaseGlazes.Where(x => x.OwnerUserId == owner.Id).MaxAsync(x => (int?)x.BaseNumber, cancellationToken) ?? 0) + 1;
        if (next > LabCode.MaxBaseNumber)
            throw new LabCodeException(409, "participant_full", $"{code} already has {LabCode.MaxBaseNumber} base glazes, the most a participant can have.");
        baseGlaze.OwnerUserId = owner.Id;
        baseGlaze.ParticipantCode = code;
        baseGlaze.BaseNumber = next;
        baseGlaze.Code = LabCode.Base(code, next);
        baseGlaze.UpdatedAt = DateTimeOffset.UtcNow;
        foreach (var experiment in await db.Experiments.Where(e => e.BaseGlazeId == baseGlaze.Id && e.DeletedAt == null).ToListAsync(cancellationToken))
        {
            experiment.ParticipantUserId = owner.Id;
            experiment.UpdatedAt = baseGlaze.UpdatedAt.Value;
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Recipe>> UpdateBaseAsync(BaseGlaze baseGlaze, BaseGlazeEdit edit, Guid actorId, CancellationToken cancellationToken)
    {
        if (edit.Name is not null)
        {
            if (string.IsNullOrWhiteSpace(edit.Name) || edit.Name.Trim().Length > 180) throw new LabCodeException(400, "invalid_name", "Base name is required (max 180 characters).");
            baseGlaze.Name = edit.Name.Trim();
        }
        if (edit.Description is not null) baseGlaze.Description = edit.Description.Trim();
        if (edit.ChangeNote is not null)
        {
            var note = edit.ChangeNote.Trim();
            if (note.Length == 0 && baseGlaze.DerivedFromBaseId is not null)
                throw new LabCodeException(400, "change_note_required", "A derived base keeps a note of what changed relative to the base it comes from.");
            if (note.Length > 500) throw new LabCodeException(400, "invalid_change_note", "Keep the change note to 500 characters.");
            baseGlaze.ChangeNote = note.Length == 0 ? null : note;
        }

        // The firing is part of the recipe a participant follows, so it locks with the materials.
        var cone = Firing.Apply(baseGlaze.Cone, Firing.NormalizeCone(edit.Firing?.Cone));
        var atmosphere = Firing.Apply(baseGlaze.Atmosphere, Firing.NormalizeAtmosphere(edit.Firing?.Atmosphere));
        if (cone != baseGlaze.Cone || atmosphere != baseGlaze.Atmosphere)
        {
            var locks = await FormulationLocksAsync(db, baseGlaze.Id, cancellationToken);
            if (locks.Count > 0)
                throw new LabCodeException(409, "base_in_use",
                    $"{baseGlaze.Code} is already in use ({string.Join("; ", locks.Take(3))}{(locks.Count > 3 ? $"; and {locks.Count - 3} more" : "")}), " +
                    $"so its firing stays as it is. Register a new base from {baseGlaze.Code} instead.");
            baseGlaze.Cone = cone;
            baseGlaze.Atmosphere = atmosphere;
            await MarkMixChangedAsync(baseGlaze.Id, cancellationToken);
        }

        var rebuilt = new List<Recipe>();
        if (edit.Formulation is not null)
        {
            ValidateRows(edit.Formulation, "formulation", requireAny: true);
            var rows = Normalize(edit.Formulation);
            var json = JsonSerializer.Serialize(rows, Json);
            if (json != baseGlaze.FormulationJson)
            {
                var locks = await FormulationLocksAsync(db, baseGlaze.Id, cancellationToken);
                if (locks.Count > 0)
                    throw new LabCodeException(409, "base_in_use",
                        $"{baseGlaze.Code} is already in use ({string.Join("; ", locks.Take(3))}{(locks.Count > 3 ? $"; and {locks.Count - 3} more" : "")}), " +
                        $"so its materials stay as they are. Register a new base from {baseGlaze.Code} instead.");
                baseGlaze.FormulationJson = json;
                var variations = await db.Recipes.Where(x => x.BaseGlazeId == baseGlaze.Id).ToListAsync(cancellationToken);
                var now = DateTimeOffset.UtcNow;
                foreach (var variation in variations)
                {
                    Rebuild(variation, baseGlaze.Code, rows, JsonSerializer.Deserialize<List<MaterialRow>>(variation.AdditionsJson, Json) ?? []);
                    variation.UpdatedAt = now;
                    variation.MixChangedAt = now;
                    rebuilt.Add(variation);
                }
            }
        }
        baseGlaze.UpdatedAt = DateTimeOffset.UtcNow;
        baseGlaze.UpdatedBy = actorId;
        await db.SaveChangesAsync(cancellationToken);
        return rebuilt;
    }

    /// <summary>A variation's full recipe from its base's rows and its own additions, keeping anything else it stores (an import's provenance, say).</summary>
    private static void Rebuild(Recipe variation, string baseCode, IReadOnlyList<MaterialRow> baseRows, IReadOnlyList<MaterialRow> additions)
    {
        var recipeJson = JsonNode.Parse(string.IsNullOrWhiteSpace(variation.RecipeJson) ? "{}" : variation.RecipeJson) as JsonObject ?? new JsonObject();
        recipeJson["baseCode"] = baseCode;
        recipeJson["baseFormulation"] = JsonSerializer.SerializeToNode(baseRows, Json);
        recipeJson["additions"] = JsonSerializer.SerializeToNode(additions, Json);
        recipeJson["full"] = JsonSerializer.SerializeToNode(Combine(baseRows, additions), Json);
        variation.RecipeJson = recipeJson.ToJsonString(Json);
    }

    /// <summary>What goes into every variation of the base has changed (its firing): participants who started one are told.</summary>
    private async Task MarkMixChangedAsync(Guid baseId, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var variation in await db.Recipes.Where(x => x.BaseGlazeId == baseId).ToListAsync(cancellationToken))
        {
            variation.MixChangedAt = now;
            variation.UpdatedAt = now;
        }
    }

    /// <summary>
    /// Why the base's materials can no longer change, one line per variation in use, in number order: a tile of it an
    /// admin has checked and approved, or a photo of it. Until the admin approves, everything stays editable even while
    /// a participant is mixing it (meeting 2026-11-02); they are told the recipe changed.
    /// </summary>
    public static Task<IReadOnlyList<string>> FormulationLocksAsync(GlazionDbContext db, Guid baseId, CancellationToken cancellationToken) =>
        InUseAsync(db, db.Recipes.Where(r => r.BaseGlazeId == baseId), cancellationToken);

    /// <summary>The same reasons for one recipe: why its own firing can no longer change.</summary>
    public static Task<IReadOnlyList<string>> RecipeLocksAsync(GlazionDbContext db, Guid recipeId, CancellationToken cancellationToken) =>
        InUseAsync(db, db.Recipes.Where(r => r.Id == recipeId), cancellationToken);

    /// <summary>
    /// A recipe's firing, edited: null leaves a value, "" clears it (a variation then fires as its base). Refused with
    /// 409 recipe_in_use once one of its tiles has been checked and approved, or photographed.
    /// </summary>
    public async Task SetRecipeFiringAsync(Recipe recipe, FiringInput firing, CancellationToken cancellationToken)
    {
        var cone = Firing.Apply(recipe.Cone, Firing.NormalizeCone(firing.Cone));
        var atmosphere = Firing.Apply(recipe.Atmosphere, Firing.NormalizeAtmosphere(firing.Atmosphere));
        if (cone == recipe.Cone && atmosphere == recipe.Atmosphere) return;
        var locks = await RecipeLocksAsync(db, recipe.Id, cancellationToken);
        if (locks.Count > 0)
            throw new LabCodeException(409, "recipe_in_use", $"{locks[0]}, so its firing stays as it is. Make a new variation for a different firing.");
        recipe.Cone = cone;
        recipe.Atmosphere = atmosphere;
        recipe.MixChangedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Replaces a variation's additions (the colorants on top of its base) and rebuilds its full recipe; true when they
    /// changed. Refused with 409 recipe_in_use once one of its tiles has been checked and approved, or photographed.
    /// </summary>
    public async Task<bool> SetAdditionsAsync(Recipe recipe, IReadOnlyList<MaterialRow> additions, CancellationToken cancellationToken)
    {
        if (recipe.BaseGlazeId is null) throw new LabCodeException(400, "not_a_variation", "Only a variation of a registered base has additions to change.");
        ValidateRows(additions, "additions", requireAny: false);
        var rows = Normalize(additions);
        var json = JsonSerializer.Serialize(rows, Json);
        if (json == recipe.AdditionsJson) return false;
        var locks = await RecipeLocksAsync(db, recipe.Id, cancellationToken);
        if (locks.Count > 0)
            throw new LabCodeException(409, "recipe_in_use", $"{locks[0]}, so what goes in it stays as it is. Add a new variation to try a change.");
        var baseGlaze = recipe.BaseGlaze ?? await db.BaseGlazes.SingleAsync(x => x.Id == recipe.BaseGlazeId, cancellationToken);
        recipe.AdditionsJson = json;
        Rebuild(recipe, baseGlaze.Code, JsonSerializer.Deserialize<List<MaterialRow>>(baseGlaze.FormulationJson, Json) ?? [], rows);
        recipe.MixChangedAt = recipe.UpdatedAt = DateTimeOffset.UtcNow;
        return true;
    }

    // ---- deleting and restoring (meeting 2026-11-02) ----

    /// <summary>
    /// Deletes a recipe an admin no longer wants: it becomes inactive and leaves every list, and its open tiles are
    /// cancelled with it. Its number stays taken; RestoreRecipeAsync brings it back. Refused (409 recipe_in_use) once
    /// one of its tiles has been checked and approved, or photographed.
    /// </summary>
    public async Task DeleteRecipeAsync(Recipe recipe, Guid adminId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (recipe.DeletedAt is not null) return;
        var locks = await RecipeLocksAsync(db, recipe.Id, cancellationToken);
        if (locks.Count > 0) throw new LabCodeException(409, "recipe_in_use", $"{locks[0]}, so it stays in the records.");
        CancelOpenTiles(await db.TileTasks.Where(t => t.RecipeId == recipe.Id && t.CancelledAt == null).ToListAsync(cancellationToken), recipe, adminId, now);
        recipe.DeletedAt = now;
        recipe.DeletedBy = adminId;
        recipe.IsActive = false;
        recipe.UpdatedAt = now;
    }

    /// <summary>Brings a deleted recipe back, with the tiles its deletion cancelled (unless the same tile was given out again since).</summary>
    public async Task RestoreRecipeAsync(Recipe recipe, Guid adminId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (recipe.DeletedAt is not { } deletedAt) return;
        var baseGlaze = recipe.BaseGlaze ?? (recipe.BaseGlazeId is { } baseId ? await db.BaseGlazes.SingleAsync(x => x.Id == baseId, cancellationToken) : null);
        if (baseGlaze?.DeletedAt is not null)
            throw new LabCodeException(409, "base_deleted", $"{baseGlaze.Code} is deleted. Restore the base first; its variations come back with it.");
        RestoreTiles(await db.TileTasks.Where(t => t.RecipeId == recipe.Id).ToListAsync(cancellationToken), deletedAt, recipe, adminId, now);
        recipe.DeletedAt = null;
        recipe.DeletedBy = null;
        recipe.IsActive = true;
        recipe.UpdatedAt = now;
    }

    /// <summary>
    /// Deletes a base with all its variations (and their open tiles). Refused (409 base_in_use) once a tile of any of
    /// them has been checked and approved, or photographed. Its number stays taken.
    /// With keepPhotos the base leaves the register all the same (meeting 2026-11-10): the variations in use stay, with
    /// their photos in the library and search, but their open tiles are cancelled and participants stop seeing them; the
    /// rest are deleted as usual. Restoring the base brings it all back.
    /// </summary>
    public async Task DeleteBaseAsync(BaseGlaze baseGlaze, Guid adminId, CancellationToken cancellationToken, bool keepPhotos = false)
    {
        if (baseGlaze.DeletedAt is not null) return;
        var inUse = new HashSet<Guid>();
        if (keepPhotos)
            inUse = (await InUseIdsAsync(db, db.Recipes.Where(r => r.BaseGlazeId == baseGlaze.Id), cancellationToken)).ToHashSet();
        else
        {
            var locks = await FormulationLocksAsync(db, baseGlaze.Id, cancellationToken);
            if (locks.Count > 0)
                throw new LabCodeException(409, "base_in_use", $"{baseGlaze.Code} is already in use ({string.Join("; ", locks.Take(3))}{(locks.Count > 3 ? $"; and {locks.Count - 3} more" : "")}), so it stays in the records.");
        }
        var now = DateTimeOffset.UtcNow;
        foreach (var variation in await db.Recipes.Where(x => x.BaseGlazeId == baseGlaze.Id && x.DeletedAt == null).ToListAsync(cancellationToken))
        {
            if (inUse.Contains(variation.Id)) CancelOpenTiles(await db.TileTasks.Where(t => t.RecipeId == variation.Id && t.CancelledAt == null).ToListAsync(cancellationToken), variation, adminId, now);
            else await DeleteRecipeAsync(variation, adminId, now, cancellationToken);
        }
        // Its cup experiments go with it (the participant stops seeing them) and come back with it.
        foreach (var experiment in await db.Experiments.Where(x => x.BaseGlazeId == baseGlaze.Id && x.DeletedAt == null).ToListAsync(cancellationToken))
        {
            experiment.DeletedAt = now;
            experiment.UpdatedAt = now;
        }
        baseGlaze.DeletedAt = now;
        baseGlaze.DeletedBy = adminId;
        await db.SaveChangesAsync(cancellationToken);
    }

    private void CancelOpenTiles(IEnumerable<TileTask> open, Recipe recipe, Guid adminId, DateTimeOffset now)
    {
        foreach (var task in open)
        {
            task.CancelledAt = now;
            task.UpdatedAt = now;
            task.ConcurrencyStamp = Guid.NewGuid();
            db.TileTaskEvents.Add(TileTaskService.Event(task, TileTaskEventKind.Cancelled, adminId, now, note: $"{recipe.Code ?? recipe.Name} was deleted"));
        }
    }

    private void RestoreTiles(List<TileTask> tasks, DateTimeOffset cancelledAt, Recipe recipe, Guid adminId, DateTimeOffset now)
    {
        foreach (var task in tasks.Where(t => t.CancelledAt == cancelledAt))
        {
            if (tasks.Any(t => t.CancelledAt == null && t.ClayBody == task.ClayBody)) continue;
            task.CancelledAt = null;
            task.UpdatedAt = now;
            task.ConcurrencyStamp = Guid.NewGuid();
            db.TileTaskEvents.Add(TileTaskService.Event(task, TileTaskEventKind.Restored, adminId, now, note: $"{recipe.Code ?? recipe.Name} was restored"));
        }
    }

    /// <summary>Brings a deleted base back with the variations deleted along with it.</summary>
    public async Task RestoreBaseAsync(BaseGlaze baseGlaze, Guid adminId, CancellationToken cancellationToken)
    {
        if (baseGlaze.DeletedAt is not { } deletedAt) return;
        baseGlaze.DeletedAt = null;
        baseGlaze.DeletedBy = null;
        var now = DateTimeOffset.UtcNow;
        foreach (var variation in await db.Recipes.Where(x => x.BaseGlazeId == baseGlaze.Id && x.DeletedAt == deletedAt).ToListAsync(cancellationToken))
        {
            variation.BaseGlaze = baseGlaze;
            await RestoreRecipeAsync(variation, adminId, now, cancellationToken);
        }
        // The variations kept for their photos get back the tiles cancelled with the base.
        foreach (var variation in await db.Recipes.Where(x => x.BaseGlazeId == baseGlaze.Id && x.DeletedAt == null).ToListAsync(cancellationToken))
            RestoreTiles(await db.TileTasks.Where(t => t.RecipeId == variation.Id).ToListAsync(cancellationToken), deletedAt, variation, adminId, now);
        foreach (var experiment in await db.Experiments.Where(x => x.BaseGlazeId == baseGlaze.Id && x.DeletedAt == deletedAt).ToListAsync(cancellationToken))
        {
            experiment.DeletedAt = null;
            experiment.UpdatedAt = now;
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>The variations in use: a tile of it checked and approved, or a photo of it.</summary>
    private static async Task<IReadOnlyList<Guid>> InUseIdsAsync(GlazionDbContext db, IQueryable<Recipe> variations, CancellationToken cancellationToken)
    {
        var (approved, photographed) = await ApprovedAndPhotographedAsync(db, variations, cancellationToken);
        return approved.Concat(photographed).Distinct().ToList();
    }

    private static async Task<(List<Guid> Approved, List<Guid> Photographed)> ApprovedAndPhotographedAsync(GlazionDbContext db, IQueryable<Recipe> variations, CancellationToken cancellationToken)
    {
        // A tile locks its recipe once an admin has checked it and approved it (meeting 2026-11-02), cancelled since or not.
        var approved = await db.TileTasks.AsNoTracking()
            .Where(t => t.ApprovedAt != null && variations.Any(v => v.Id == t.RecipeId))
            .Select(t => t.RecipeId).Distinct().ToListAsync(cancellationToken);
        var photographed = await variations
            .Where(r => db.Uploads.Any(u => u.RecipeId == r.Id && TileTaskService.Sent.Contains(u.Status)) || db.RecipeImages.Any(i => i.RecipeId == r.Id))
            .Select(r => r.Id).ToListAsync(cancellationToken);
        return (approved, photographed);
    }

    private static async Task<IReadOnlyList<string>> InUseAsync(GlazionDbContext db, IQueryable<Recipe> variations, CancellationToken cancellationToken)
    {
        var (approved, photographed) = await ApprovedAndPhotographedAsync(db, variations, cancellationToken);
        var ids = approved.Concat(photographed).ToHashSet();
        if (ids.Count == 0) return [];
        var named = await variations.Where(r => ids.Contains(r.Id)).OrderBy(r => r.RecipeNumber)
            .Select(r => new { r.Id, Label = r.Code ?? r.Name }).ToListAsync(cancellationToken);
        return named.Select(r => photographed.Contains(r.Id) ? $"{r.Label} has been photographed" : $"{r.Label} has been checked and approved").ToList();
    }

    public const int MaxVariationsPerRequest = 100;

    public async Task<Recipe> RegisterVariationAsync(BaseGlaze baseGlaze, string name, string? description, IReadOnlyList<MaterialRow> additions, Guid? ownerUserId, CancellationToken cancellationToken) =>
        (await RegisterVariationsAsync(baseGlaze, [new VariationInput(name, description, additions)], ownerUserId, cancellationToken))[0];

    /// <summary>
    /// Numbers a run of variations under one base in request order (a series such as a stain at 2, 5, 7.5 and 10%).
    /// Every input is validated first and all are saved together, so a run is never half-numbered.
    /// </summary>
    public async Task<IReadOnlyList<Recipe>> RegisterVariationsAsync(BaseGlaze baseGlaze, IReadOnlyList<VariationInput> variations, Guid? ownerUserId, CancellationToken cancellationToken)
    {
        if (variations.Count == 0) throw new LabCodeException(400, "variations_required", "Give at least one variation.");
        if (baseGlaze.DeletedAt is not null) throw new LabCodeException(409, "base_deleted", $"{baseGlaze.Code} is deleted. Restore it first to add variations.");
        if (variations.Count > MaxVariationsPerRequest) throw new LabCodeException(400, "too_many_variations", $"At most {MaxVariationsPerRequest} variations per request.");
        for (var i = 0; i < variations.Count; i++)
        {
            var v = variations[i];
            if (string.IsNullOrWhiteSpace(v.Name) || v.Name.Trim().Length > 180)
                throw new LabCodeException(400, "invalid_name", variations.Count == 1 ? "Recipe name is required (max 180 characters)." : $"Variation {i + 1}: a name is required (max 180 characters).");
            ValidateRows(v.Additions ?? [], "additions", requireAny: false);
        }
        var baseRows = JsonSerializer.Deserialize<List<MaterialRow>>(baseGlaze.FormulationJson, Json) ?? [];

        for (var attempt = 1; ; attempt++)
        {
            var first = (await db.Recipes.Where(x => x.BaseGlazeId == baseGlaze.Id).MaxAsync(x => x.RecipeNumber, cancellationToken) ?? 0) + 1;
            var last = first + variations.Count - 1;
            if (last > LabCode.MaxRecipeNumber)
                throw new LabCodeException(409, "base_full", first > LabCode.MaxRecipeNumber
                    ? $"{baseGlaze.Code} already has {LabCode.MaxRecipeNumber} variations. Register a new base."
                    : $"{baseGlaze.Code} has room for {LabCode.MaxRecipeNumber - first + 1} more variations, not {variations.Count}.");
            var entities = variations.Select((v, i) =>
            {
                var additionRows = Normalize(v.Additions ?? []);
                var number = first + i;
                // Its own firing only where it differs from the base's; otherwise it follows the base.
                var cone = Firing.Apply(null, Firing.NormalizeCone(v.Firing?.Cone));
                var atmosphere = Firing.Apply(null, Firing.NormalizeAtmosphere(v.Firing?.Atmosphere));
                return new Recipe
                {
                    Id = Guid.CreateVersion7(),
                    Name = v.Name.Trim(),
                    Description = v.Description?.Trim() ?? string.Empty,
                    SourceType = RecipeSourceType.Manual,
                    OwnerUserId = ownerUserId,
                    BaseGlazeId = baseGlaze.Id,
                    RecipeNumber = number,
                    Code = LabCode.Recipe(baseGlaze.ParticipantCode, baseGlaze.BaseNumber, number),
                    AdditionsJson = JsonSerializer.Serialize(additionRows, Json),
                    Cone = cone == baseGlaze.Cone ? null : cone,
                    Atmosphere = atmosphere == baseGlaze.Atmosphere ? null : atmosphere,
                    RecipeJson = JsonSerializer.Serialize(new { baseCode = baseGlaze.Code, baseFormulation = baseRows, additions = additionRows, full = Combine(baseRows, additionRows) }, Json)
                };
            }).ToList();
            db.Recipes.AddRange(entities);
            try { await db.SaveChangesAsync(cancellationToken); return entities; }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex) && attempt < 3)
            {
                foreach (var e in entities) db.Entry(e).State = EntityState.Detached; // another request took these numbers; take the next ones
            }
        }
    }

    public async Task<Recipe?> FindByCodeAsync(string? code, CancellationToken cancellationToken)
    {
        var normalized = LabCode.NormalizeRecipe(code);
        return normalized is null ? null : await db.Recipes.Include(x => x.BaseGlaze).SingleOrDefaultAsync(x => x.Code == normalized, cancellationToken);
    }

    /// <summary>Base rows plus additions; a material present in both is listed once with the summed amount (same unit required).</summary>
    public static IReadOnlyList<MaterialRow> Combine(IReadOnlyList<MaterialRow> baseRows, IReadOnlyList<MaterialRow> additions)
    {
        var result = new List<MaterialRow>(baseRows);
        foreach (var add in additions)
        {
            var i = result.FindIndex(r => string.Equals(r.RawName, add.RawName, StringComparison.OrdinalIgnoreCase) && string.Equals(r.Unit, add.Unit, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) result[i] = result[i] with { Amount = result[i].Amount + add.Amount };
            else result.Add(add);
        }
        return result;
    }

    private static List<MaterialRow> Normalize(IReadOnlyList<MaterialRow> rows) => rows.Select(r => new MaterialRow(r.RawName.Trim(), r.Amount, r.Unit.Trim())).ToList();

    private static void ValidateRows(IReadOnlyList<MaterialRow>? rows, string field, bool requireAny)
    {
        if (rows is null || rows.Count == 0) { if (requireAny) throw new LabCodeException(400, "formulation_required", $"At least one material row is required in {field}."); return; }
        if (rows.Count > 200) throw new LabCodeException(400, "too_many_rows", $"{field} may contain at most 200 rows.");
        foreach (var row in rows)
        {
            if (string.IsNullOrWhiteSpace(row.RawName) || row.RawName.Length > 255) throw new LabCodeException(400, "invalid_material_name", $"Every {field} row needs a material name (max 255 characters).");
            if (row.Amount < 0) throw new LabCodeException(400, "invalid_amount", $"Amount for '{row.RawName}' must be zero or positive.");
            if (string.IsNullOrWhiteSpace(row.Unit) || !Units.Contains(row.Unit.Trim())) throw new LabCodeException(400, "invalid_unit", $"Unit for '{row.RawName}' must be one of %, g, kg, oz, lb.");
        }
    }

    private static bool IsUniqueViolation(DbUpdateException ex) => ex.InnerException is Npgsql.PostgresException { SqlState: "23505" };
}
