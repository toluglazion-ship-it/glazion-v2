using System.Text.Json;
using Glazion.Api.Chemistry;
using Glazion.Api.Contracts;
using Glazion.Api.Data;
using Glazion.Api.Domain;
using Glazion.Api.Experiments;
using Glazion.Api.Infrastructure;
using Glazion.Api.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Glazion.Api.Controllers;

[ApiController]
[Route("api/v2/admin")]
[Authorize(Roles = "Admin")]
public sealed class AdminController(GlazionDbContext db, IPasswordService passwords, LabCodeService labCodes, AuthService auth, ILogger<AdminController> logger) : ControllerBase
{
    /// <summary>Uploads an administrator still has to act on, or that are still moving towards review.</summary>
    internal static readonly UploadStatus[] PendingReviewStatuses = [UploadStatus.Uploaded, UploadStatus.Validating, UploadStatus.NeedsReview];

    [HttpGet("overview")]
    public async Task<ActionResult<OverviewDto>> Overview(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        return Ok(new OverviewDto(
            await db.Users.CountAsync(cancellationToken),
            await db.CaptureCodes.Usable(now).CountAsync(cancellationToken),
            await db.Recipes.CountAsync(x => x.IsActive, cancellationToken),
            await db.Uploads.CountAsync(x => x.Status != UploadStatus.Cancelled && x.Status != UploadStatus.Created, cancellationToken),
            await db.Uploads.CountAsync(x => PendingReviewStatuses.Contains(x.Status), cancellationToken)));
    }

    // ---- Users ----

    [HttpGet("users")]
    public Task<PagedResult<UserDto>> Users([FromQuery] string? cursor, [FromQuery] int? pageSize, [FromQuery] AccountStatus? status, [FromQuery] string? search, CancellationToken cancellationToken)
    {
        var query = db.Users.AsNoTracking();
        if (status is not null) query = query.Where(x => x.Status == status);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLower();
            query = query.Where(x => x.Username.ToLower().Contains(term) || x.DisplayName.ToLower().Contains(term) || (x.Email != null && x.Email.Contains(term)));
        }
        return Paging.PageAsync(query.OrderBy(x => x.Username).ThenBy(x => x.Id), cursor, pageSize,
            rows => Task.FromResult<IReadOnlyList<UserDto>>(rows.Select(Views.User).ToList()), cancellationToken);
    }

    [HttpGet("users/{userId:guid}")]
    public async Task<ActionResult<UserDto>> GetUser(Guid userId, CancellationToken cancellationToken) =>
        Ok(Views.User(await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == userId, cancellationToken) ?? throw ApiProblems.NotFound("User")));

    /// <summary>Accounts an administrator creates are Active straight away.</summary>
    [HttpPost("users")]
    public async Task<ActionResult<UserDto>> CreateUser(CreateUserRequest request, CancellationToken cancellationToken)
    {
        ContractValidation.ValidateCredentials(request.Username, request.Password);
        var username = request.Username.Trim();
        if (await db.Users.AnyAsync(x => x.Username == username, cancellationToken)) throw ApiProblems.Conflict("username_taken", "That username is already taken.");
        string? email = null;
        if (!string.IsNullOrWhiteSpace(request.Email))
        {
            email = request.Email.Trim().ToLowerInvariant();
            if (email.Length > 320 || !System.Net.Mail.MailAddress.TryCreate(email, out _)) throw ApiProblems.Validation("email", "A valid email address is required.");
            if (await db.Users.AnyAsync(x => x.Email == email, cancellationToken)) throw ApiProblems.Conflict("email_taken", "An account with that email address already exists.");
        }
        var displayName = string.IsNullOrWhiteSpace(request.DisplayName) ? username : request.DisplayName.Trim();
        if (displayName.Length > 160) throw ApiProblems.Validation("displayName", "Display name must be at most 160 characters.");
        var user = new AppUser { Id = Guid.NewGuid(), Username = username, DisplayName = displayName, Email = email, PasswordHash = passwords.Hash(request.Password), Role = UserRole.User, Status = AccountStatus.Active };
        // Without a code typed in, the user gets the next free one (meeting 2026-11-09), so they can be given bases straight away.
        await labCodes.AssignParticipantCodeAsync(user, string.IsNullOrWhiteSpace(request.ParticipantCode) ? await labCodes.NextParticipantCodeAsync(cancellationToken) : request.ParticipantCode, cancellationToken);
        db.Users.Add(user);
        await db.SaveChangesAsync(cancellationToken);
        return Created($"/api/v2/admin/users/{user.Id}", Views.User(user));
    }

    /// <summary>Activate, suspend or disable an account. Leaving Active signs the account out everywhere.</summary>
    [HttpPatch("users/{userId:guid}/status")]
    public async Task<IActionResult> SetStatus(Guid userId, SetStatusRequest request, CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(request.Status)) throw ApiProblems.Validation("status", "Unknown account status.");
        var user = await db.Users.SingleOrDefaultAsync(x => x.Id == userId, cancellationToken) ?? throw ApiProblems.NotFound("User");
        if (user.Id == User.GetUserId() && request.Status != AccountStatus.Active)
            throw ApiProblems.Conflict("cannot_change_own_status", "You cannot suspend or disable your own account.");
        if (user.Status == request.Status) return NoContent();
        user.Status = request.Status;
        user.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        if (request.Status is AccountStatus.Suspended or AccountStatus.Disabled) await auth.RevokeAllAsync(user.Id, cancellationToken);
        logger.LogInformation("User {UserId} status set to {Status} by {AdminId}", user.Id, request.Status, User.GetUserId());
        return NoContent();
    }

    /// <summary>
    /// Sets a new password for a user who has forgotten theirs (8–256 characters). The user is signed out everywhere and
    /// signs in again with the new password. Setting your own signs you out too.
    /// </summary>
    [HttpPost("users/{userId:guid}/password")]
    public async Task<ActionResult<SetPasswordResponse>> SetPassword(Guid userId, SetPasswordRequest request, CancellationToken cancellationToken)
    {
        var ended = await auth.SetPasswordAsync(userId, request.Password, cancellationToken);
        logger.LogInformation("Password for user {UserId} set by {AdminId}; {Sessions} sessions ended", userId, User.GetUserId(), ended);
        return Ok(new SetPasswordResponse(ended));
    }

    /// <summary>Participant code (A03) and display name. The participant code is set once and then fixed.</summary>
    [HttpPatch("users/{userId:guid}")]
    public async Task<ActionResult<UserDto>> UpdateUser(Guid userId, UpdateUserRequest request, CancellationToken cancellationToken)
    {
        var user = await db.Users.SingleOrDefaultAsync(x => x.Id == userId, cancellationToken) ?? throw ApiProblems.NotFound("User");
        if (!string.IsNullOrWhiteSpace(request.ParticipantCode)) await labCodes.AssignParticipantCodeAsync(user, request.ParticipantCode, cancellationToken);
        if (!string.IsNullOrWhiteSpace(request.DisplayName))
        {
            if (request.DisplayName.Trim().Length > 160) throw ApiProblems.Validation("displayName", "Display name must be at most 160 characters.");
            user.DisplayName = request.DisplayName.Trim();
        }
        user.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return Ok(Views.User(user));
    }

    // ---- Recipes ----

    /// <summary>
    /// Newest first, each with its base's name and its live tile tasks (who has it, how far it has got). search matches
    /// the recipe's name or code and its base glaze's name ("bamboo" lists every Bamboo variation). baseCode: one base's
    /// variations. assignedUserId: recipes with a live task for that person. assigned=false: recipes nobody has yet.
    /// taskStage: recipes with a task at that stage; overdue=true: with a task past its due date.
    /// </summary>
    [HttpGet("recipes")]
    public Task<PagedResult<RecipeDto>> Recipes([FromQuery] string? cursor, [FromQuery] int? pageSize, [FromQuery] bool? activeOnly, [FromQuery] string? search,
        [FromQuery] string? baseCode, [FromQuery] Guid? assignedUserId, [FromQuery] bool? assigned, [FromQuery] TileTaskStage? taskStage, [FromQuery] bool? overdue,
        [FromServices] TileTaskService tasks, CancellationToken cancellationToken, [FromQuery] bool? deleted = null)
    {
        // Deleted recipes leave the list; deleted=true lists only them, to restore.
        var query = db.Recipes.AsNoTracking().Include(x => x.BaseGlaze).Where(x => deleted == true ? x.DeletedAt != null : x.DeletedAt == null);
        if (activeOnly == true) query = query.Where(x => x.IsActive);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLower();
            query = query.Where(x => x.Name.ToLower().Contains(term) || (x.Code != null && x.Code.ToLower().Contains(term))
                || (x.BaseGlaze != null && x.BaseGlaze.Name.ToLower().Contains(term)));
        }
        if (!string.IsNullOrWhiteSpace(baseCode))
        {
            var code = baseCode.Trim().ToUpperInvariant();
            query = query.Where(x => x.BaseGlaze != null && x.BaseGlaze.Code == code);
        }
        var live = db.TileTasks.Where(t => t.CancelledAt == null);
        if (assignedUserId is { } userId) query = query.Where(x => live.Any(t => t.RecipeId == x.Id && t.AssignedUserId == userId));
        if (assigned is { } a) query = a ? query.Where(x => live.Any(t => t.RecipeId == x.Id)) : query.Where(x => !live.Any(t => t.RecipeId == x.Id));
        if (taskStage is { } stage)
        {
            var atStage = TileTaskService.WhereStage(TileTaskService.Rows(db.TileTasks), stage).Select(r => r.Task.RecipeId);
            query = query.Where(x => atStage.Contains(x.Id));
        }
        if (overdue == true)
        {
            var late = TileTaskService.WhereOverdue(TileTaskService.Rows(db.TileTasks), TileTaskService.Today).Select(r => r.Task.RecipeId);
            query = query.Where(x => late.Contains(x.Id));
        }
        var ordered = deleted == true ? query.OrderByDescending(x => x.DeletedAt).ThenBy(x => x.Id) : query.OrderByDescending(x => x.CreatedAt).ThenBy(x => x.Id);
        return Paging.PageAsync(ordered, cursor, pageSize, async rows =>
        {
            // The page's live tasks in one go (three queries), not per recipe.
            var ids = rows.Select(x => x.Id).ToList();
            var taskRows = await TileTaskService.Ordered(TileTaskService.Rows(db.TileTasks.AsNoTracking().Where(t => ids.Contains(t.RecipeId) && t.CancelledAt == null)))
                .ToListAsync(cancellationToken);
            var byRecipe = (await tasks.MapAsync(taskRows, false, cancellationToken)).ToLookup(t => t.RecipeId);
            return (IReadOnlyList<RecipeDto>)rows.Select(x => Views.Recipe(x) with { BaseName = x.BaseGlaze?.Name, Tasks = byRecipe[x.Id].ToList() }).ToList();
        }, cancellationToken);
    }

    [HttpPost("recipes")]
    public async Task<ActionResult<RecipeDto>> CreateRecipe(CreateRecipeRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 180) throw ApiProblems.Validation("name", "A recipe name of at most 180 characters is required.");
        var entity = new Recipe
        {
            Id = Guid.NewGuid(), Name = request.Name.Trim(), Description = request.Description?.Trim() ?? string.Empty, RecipeJson = NormalizeRecipeJson(request.RecipeJson),
            Cone = Firing.Apply(null, Firing.NormalizeCone(request.Cone)), Atmosphere = Firing.Apply(null, Firing.NormalizeAtmosphere(request.Atmosphere))
        };
        db.Recipes.Add(entity);
        await db.SaveChangesAsync(cancellationToken);
        return Created($"/api/v2/admin/recipes/{entity.Id}", Views.Recipe(entity, null));
    }

    /// <summary>One recipe, with whether what goes in it can still change (mixEditable) and why not.</summary>
    [HttpGet("recipes/{recipeId:guid}")]
    public async Task<ActionResult<RecipeDto>> GetRecipe(Guid recipeId, CancellationToken cancellationToken)
    {
        var recipe = await db.Recipes.AsNoTracking().Include(x => x.BaseGlaze).SingleOrDefaultAsync(x => x.Id == recipeId, cancellationToken) ?? throw ApiProblems.NotFound("Recipe");
        var locks = await LabCodeService.RecipeLocksAsync(db, recipeId, cancellationToken);
        return Ok(Views.Recipe(recipe) with { MixEditable = locks.Count == 0, MixLockedBecause = locks });
    }

    /// <summary>
    /// Name, description, active flag, and until a tile of it is checked and approved, its firing and (a variation) its
    /// additions. A changed mix is recalculated where a UMF was stored, and participants who started it are told.
    /// </summary>
    [HttpPatch("recipes/{recipeId:guid}")]
    public async Task<ActionResult<RecipeDto>> UpdateRecipeDetails(Guid recipeId, UpdateRecipeDetailsRequest request, [FromServices] UmfCalculationService umf, CancellationToken cancellationToken)
    {
        var recipe = await db.Recipes.Include(x => x.BaseGlaze).SingleOrDefaultAsync(x => x.Id == recipeId, cancellationToken) ?? throw ApiProblems.NotFound("Recipe");
        if (recipe.DeletedAt is not null) throw ApiProblems.Conflict("recipe_deleted", "This recipe is deleted. Restore it first.");
        if (request.Name is not null)
        {
            if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 180) throw ApiProblems.Validation("name", "A recipe name of at most 180 characters is required.");
            recipe.Name = request.Name.Trim();
        }
        if (request.Description is not null) recipe.Description = request.Description.Trim();
        if (request.Cone is not null || request.Atmosphere is not null) await labCodes.SetRecipeFiringAsync(recipe, new FiringInput(request.Cone, request.Atmosphere), cancellationToken);
        var additionsChanged = request.Additions is not null && await labCodes.SetAdditionsAsync(recipe, request.Additions, cancellationToken);
        if (request.IsActive is { } active) recipe.IsActive = active;
        recipe.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        if (additionsChanged && await db.RecipeCalculationResults.AnyAsync(x => x.RecipeId == recipe.Id, cancellationToken))
            await RecalculateAsync(umf, [recipe], User.GetUserId(), cancellationToken);
        return Ok(Views.Recipe(recipe));
    }

    /// <summary>
    /// Deletes a recipe (meeting 2026-11-02): it leaves every list and its open tiles are cancelled with it. Its code
    /// stays taken and it can be restored. 409 recipe_in_use once a tile of it is checked and approved, or photographed.
    /// </summary>
    [HttpDelete("recipes/{recipeId:guid}")]
    public async Task<ActionResult<RecipeDto>> DeleteRecipe(Guid recipeId, CancellationToken cancellationToken)
    {
        var recipe = await db.Recipes.Include(x => x.BaseGlaze).SingleOrDefaultAsync(x => x.Id == recipeId, cancellationToken) ?? throw ApiProblems.NotFound("Recipe");
        await labCodes.DeleteRecipeAsync(recipe, User.GetUserId(), DateTimeOffset.UtcNow, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Admin {AdminId} deleted recipe {Code}", User.GetUserId(), recipe.Code ?? recipe.Id.ToString());
        return Ok(Views.Recipe(recipe));
    }

    /// <summary>Brings a deleted recipe back with the tiles its deletion cancelled.</summary>
    [HttpPost("recipes/{recipeId:guid}/restore")]
    public async Task<ActionResult<RecipeDto>> RestoreRecipe(Guid recipeId, CancellationToken cancellationToken)
    {
        var recipe = await db.Recipes.Include(x => x.BaseGlaze).SingleOrDefaultAsync(x => x.Id == recipeId, cancellationToken) ?? throw ApiProblems.NotFound("Recipe");
        await labCodes.RestoreRecipeAsync(recipe, User.GetUserId(), DateTimeOffset.UtcNow, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return Ok(Views.Recipe(recipe));
    }

    /// <summary>A stored UMF of old rows would mislead search and the calculator: recalculate it. Failures are returned, not thrown.</summary>
    private static async Task<List<RecalculationFailure>> RecalculateAsync(UmfCalculationService umf, IEnumerable<Recipe> recipes, Guid adminId, CancellationToken cancellationToken)
    {
        var failures = new List<RecalculationFailure>();
        foreach (var recipe in recipes.OrderBy(r => r.RecipeNumber))
        {
            try { await umf.CalculateRecipeAsync(recipe, adminId, true, cancellationToken); }
            catch (ApiProblemException ex) { failures.Add(new RecalculationFailure(recipe.Code, ex.Code, ex.Message)); }
        }
        return failures;
    }

    [HttpGet("recipes/by-code/{code}")]
    public async Task<ActionResult<RecipeDto>> RecipeByCode(string code, CancellationToken cancellationToken) =>
        Ok(Views.Recipe(await labCodes.FindByCodeAsync(code, cancellationToken) ?? throw new ApiProblemException(404, "recipe_code_not_found", "Not found", $"Recipe code '{code}' was not found.")));

    /// <summary>Outcome, active flag and its own firing (until it is in use). The number and the formulation of a coded recipe are never edited.</summary>
    [HttpPatch("recipes/by-code/{code}")]
    public async Task<ActionResult<RecipeDto>> UpdateRecipeOutcome(string code, UpdateRecipeRequest request, CancellationToken cancellationToken)
    {
        var recipe = await labCodes.FindByCodeAsync(code, cancellationToken) ?? throw new ApiProblemException(404, "recipe_code_not_found", "Not found", $"Recipe code '{code}' was not found.");
        if (request.OutcomeNote is not null) recipe.OutcomeNote = request.OutcomeNote.Trim() is { Length: > 0 } note ? (note.Length > 500 ? note[..500] : note) : null;
        if (request.Cone is not null || request.Atmosphere is not null) await labCodes.SetRecipeFiringAsync(recipe, new FiringInput(request.Cone, request.Atmosphere), cancellationToken);
        if (request.IsActive is { } active) recipe.IsActive = active;
        recipe.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return Ok(Views.Recipe(recipe));
    }

    /// <summary>
    /// Builds on a variation: its base + additions become a new base (default: in the variation owner's register).
    /// The new base records the variation and its parent base. 409 if the variation was already promoted.
    /// </summary>
    [HttpPost("recipes/by-code/{code}/promote")]
    public async Task<ActionResult<BaseGlazeResponse>> PromoteVariation(string code, [FromBody(EmptyBodyBehavior = Microsoft.AspNetCore.Mvc.ModelBinding.EmptyBodyBehavior.Allow)] PromoteVariationRequest? request, CancellationToken cancellationToken)
    {
        var variation = await labCodes.FindByCodeAsync(code, cancellationToken) ?? throw new ApiProblemException(404, "recipe_code_not_found", "Not found", $"Recipe code '{code}' was not found.");
        AppUser? owner;
        if (!string.IsNullOrWhiteSpace(request?.ParticipantCode))
            owner = await db.Users.SingleOrDefaultAsync(x => x.ParticipantCode == LabCode.NormalizeParticipant(request.ParticipantCode), cancellationToken)
                ?? throw new ApiProblemException(400, "participant_not_found", "Participant not found", $"No participant has code {request.ParticipantCode}.");
        else
        {
            var ownerId = variation.OwnerUserId ?? variation.BaseGlaze!.OwnerUserId;
            owner = await db.Users.SingleAsync(x => x.Id == ownerId, cancellationToken);
        }
        var entity = await labCodes.PromoteVariationAsync(variation, owner, request?.Name, request?.Description, User.GetUserId(), cancellationToken);
        return Created($"/api/v2/admin/base-glazes/{entity.Code}", await Views.BaseAsync(db, entity.Id, includeRecipes: false, cancellationToken));
    }

    // ---- Base-glaze register and coded variations (docs/features/lab-code-naming.md) ----

    [HttpPost("base-glazes")]
    public async Task<ActionResult<BaseGlazeResponse>> RegisterBase(RegisterBaseGlazeRequest request, CancellationToken cancellationToken)
    {
        AppUser? owner = null;
        if (request.UserId is { } id) owner = await db.Users.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        else if (!string.IsNullOrWhiteSpace(request.ParticipantCode)) owner = await db.Users.SingleOrDefaultAsync(x => x.ParticipantCode == LabCode.NormalizeParticipant(request.ParticipantCode), cancellationToken);
        if (owner is null) throw new ApiProblemException(400, "participant_not_found", "Participant not found", "Provide the participantCode (e.g. A03) or userId of an existing participant.");
        var entity = await labCodes.RegisterBaseAsync(owner, request.Name, request.Description, request.Formulation ?? [], request.DerivedFromBaseCode, request.ChangeNote, User.GetUserId(), cancellationToken,
            new FiringInput(request.Cone, request.Atmosphere));
        return Created($"/api/v2/admin/base-glazes/{entity.Code}", await Views.BaseAsync(db, entity.Id, includeRecipes: false, cancellationToken));
    }

    /// <summary>
    /// The register, by participant then base number. A plain array (first 500) unless cursor or pageSize is sent,
    /// which returns PagedResult; the admin app still reads the array.
    /// </summary>
    [HttpGet("base-glazes")]
    [OptInPaging(typeof(BaseGlazeResponse))]
    public async Task<IActionResult> BaseGlazes([FromQuery] string? participantCode, [FromQuery] string? cursor, [FromQuery] int? pageSize, CancellationToken cancellationToken,
        [FromQuery] bool? deleted = null)
    {
        // Deleted bases leave the register; deleted=true lists only them, to restore.
        var query = db.BaseGlazes.AsNoTracking().Where(x => deleted == true ? x.DeletedAt != null : x.DeletedAt == null);
        if (!string.IsNullOrWhiteSpace(participantCode)) query = query.Where(x => x.ParticipantCode == LabCode.NormalizeParticipant(participantCode));
        var ordered = query.OrderBy(x => x.ParticipantCode).ThenBy(x => x.BaseNumber).ThenBy(x => x.Id);
        if (Paging.Requested(cursor, pageSize))
            return Ok(await Paging.PageAsync(ordered.Select(x => x.Id), cursor, pageSize, ids => Views.BasesAsync(db, ids, cancellationToken), cancellationToken));
        var all = await ordered.Select(x => x.Id).Take(500).ToListAsync(cancellationToken);
        return Ok(await Views.BasesAsync(db, all, cancellationToken));
    }

    [HttpGet("base-glazes/{code}")]
    public async Task<ActionResult<BaseGlazeResponse>> BaseGlaze(string code, CancellationToken cancellationToken)
    {
        var id = await db.BaseGlazes.AsNoTracking().Where(x => x.Code == code.Trim().ToUpperInvariant()).Select(x => (Guid?)x.Id).SingleOrDefaultAsync(cancellationToken);
        if (id is null) throw new ApiProblemException(404, "base_not_found", "Not found", $"Base {code} does not exist.");
        return Ok(await Views.BaseAsync(db, id.Value, includeRecipes: true, cancellationToken));
    }

    /// <summary>
    /// Corrects a base: name, description and change note always; the formulation only until a tile of one of its
    /// variations is made (409 base_in_use after that: register a derived base). A new formulation rebuilds every
    /// variation's full recipe and recalculates the UMF of those that had one stored.
    /// </summary>
    [HttpPatch("base-glazes/{code}")]
    public async Task<ActionResult<UpdateBaseGlazeResponse>> UpdateBaseGlaze(string code, UpdateBaseGlazeRequest request, [FromServices] UmfCalculationService umf,
        CancellationToken cancellationToken)
    {
        var baseGlaze = await db.BaseGlazes.SingleOrDefaultAsync(x => x.Code == code.Trim().ToUpperInvariant(), cancellationToken)
            ?? throw new ApiProblemException(404, "base_not_found", "Not found", $"Base {code} does not exist.");
        if (baseGlaze.DeletedAt is not null) throw ApiProblems.Conflict("base_deleted", $"{baseGlaze.Code} is deleted. Restore it first.");
        var adminId = User.GetUserId();
        var from = baseGlaze.Code;
        if (!string.IsNullOrWhiteSpace(request.ParticipantCode)) await labCodes.MoveBaseAsync(baseGlaze, request.ParticipantCode, cancellationToken);
        if (baseGlaze.Code != from) logger.LogInformation("Admin {AdminId} gave base {From} to {Participant}: now {Code}", adminId, from, baseGlaze.ParticipantCode, baseGlaze.Code);
        var rebuilt = await labCodes.UpdateBaseAsync(baseGlaze, new BaseGlazeEdit(request.Name, request.Description, request.Formulation, request.ChangeNote, new FiringInput(request.Cone, request.Atmosphere)), adminId, cancellationToken);

        // A stored UMF of the old rows would mislead search and the calculator: recalculate those that had one.
        var rebuiltIds = rebuilt.Select(r => r.Id).ToList();
        var calculated = await db.RecipeCalculationResults.Where(x => rebuiltIds.Contains(x.RecipeId)).Select(x => x.RecipeId).Distinct().ToListAsync(cancellationToken);
        var failures = await RecalculateAsync(umf, rebuilt.Where(r => calculated.Contains(r.Id)), adminId, cancellationToken);
        logger.LogInformation("Admin {AdminId} edited base {Code}: {Rebuilt} variations rebuilt, {Recalculated} recalculated, {Failed} could not be", adminId, baseGlaze.Code,
            rebuilt.Count, calculated.Count - failures.Count, failures.Count);
        return Ok(new UpdateBaseGlazeResponse(await Views.BaseAsync(db, baseGlaze.Id, includeRecipes: true, cancellationToken), rebuilt.Count, calculated.Count - failures.Count, failures));
    }

    /// <summary>
    /// Deletes a base with all its variations and their open tiles (meeting 2026-11-02). Its number stays taken and it
    /// can be restored. 409 base_in_use once a tile of any variation is checked and approved, or photographed, unless
    /// keepPhotos: then the base leaves the register and the variations in use stay with their photos.
    /// </summary>
    [HttpDelete("base-glazes/{code}")]
    public async Task<ActionResult<BaseGlazeResponse>> DeleteBase(string code, CancellationToken cancellationToken, [FromQuery] bool keepPhotos = false)
    {
        var baseGlaze = await db.BaseGlazes.SingleOrDefaultAsync(x => x.Code == code.Trim().ToUpperInvariant(), cancellationToken)
            ?? throw new ApiProblemException(404, "base_not_found", "Not found", $"Base {code} does not exist.");
        await labCodes.DeleteBaseAsync(baseGlaze, User.GetUserId(), cancellationToken, keepPhotos);
        logger.LogInformation("Admin {AdminId} deleted base {Code} (keep photos: {KeepPhotos})", User.GetUserId(), baseGlaze.Code, keepPhotos);
        return Ok(await Views.BaseAsync(db, baseGlaze.Id, includeRecipes: true, cancellationToken));
    }

    /// <summary>Brings a deleted base back with the variations (and tiles) deleted along with it.</summary>
    [HttpPost("base-glazes/{code}/restore")]
    public async Task<ActionResult<BaseGlazeResponse>> RestoreBase(string code, CancellationToken cancellationToken)
    {
        var baseGlaze = await db.BaseGlazes.SingleOrDefaultAsync(x => x.Code == code.Trim().ToUpperInvariant(), cancellationToken)
            ?? throw new ApiProblemException(404, "base_not_found", "Not found", $"Base {code} does not exist.");
        await labCodes.RestoreBaseAsync(baseGlaze, User.GetUserId(), cancellationToken);
        return Ok(await Views.BaseAsync(db, baseGlaze.Id, includeRecipes: true, cancellationToken));
    }

    [HttpPost("base-glazes/{code}/recipes")]
    public async Task<ActionResult<RecipeDto>> RegisterVariation(string code, RegisterVariationRequest request, CancellationToken cancellationToken)
    {
        var baseGlaze = await db.BaseGlazes.SingleOrDefaultAsync(x => x.Code == code.Trim().ToUpperInvariant(), cancellationToken)
            ?? throw new ApiProblemException(404, "base_not_found", "Not found", $"Base {code} does not exist.");
        var recipe = (await labCodes.RegisterVariationsAsync(baseGlaze, [new VariationInput(request.Name, request.Description, request.Additions ?? [], new FiringInput(request.Cone, request.Atmosphere))],
            baseGlaze.OwnerUserId, cancellationToken))[0];
        return Created($"/api/v2/admin/recipes/by-code/{recipe.Code}", Views.Recipe(recipe, baseGlaze.Code));
    }

    /// <summary>
    /// Several variations of one base, numbered together in request order. With captureCodeId every new recipe is also
    /// assigned to that capture code, so the tester sees them in the capture app straight away.
    /// </summary>
    [HttpPost("base-glazes/{code}/recipes/batch")]
    public async Task<ActionResult<IReadOnlyList<RecipeDto>>> RegisterVariations(string code, RegisterVariationsRequest request, CancellationToken cancellationToken)
    {
        var baseGlaze = await db.BaseGlazes.SingleOrDefaultAsync(x => x.Code == code.Trim().ToUpperInvariant(), cancellationToken)
            ?? throw new ApiProblemException(404, "base_not_found", "Not found", $"Base {code} does not exist.");
        CaptureCode? captureCode = null;
        if (request.CaptureCodeId is { } codeId)
        {
            captureCode = await db.CaptureCodes.SingleOrDefaultAsync(x => x.Id == codeId, cancellationToken) ?? throw ApiProblems.NotFound("Capture code");
            if (!Views.IsUsable(captureCode, DateTimeOffset.UtcNow)) throw ApiProblems.Conflict("code_not_active", "This capture code is revoked or expired.");
        }
        var recipes = await labCodes.RegisterVariationsAsync(baseGlaze, ToInputs(request), baseGlaze.OwnerUserId, cancellationToken);
        if (captureCode is not null)
        {
            db.CodeRecipes.AddRange(recipes.Select(r => new CodeRecipe { CaptureCodeId = captureCode.Id, RecipeId = r.Id }));
            await db.SaveChangesAsync(cancellationToken);
        }
        return StatusCode(201, recipes.Select(r => Views.Recipe(r, baseGlaze.Code)).ToList());
    }

    /// <summary>
    /// Register a base (or pick one), number its variations and assign them, in one transaction: if any part is refused
    /// (a duplicate name, a full base, an unknown participant), nothing is saved and the form can be sent again.
    /// </summary>
    [HttpPost("base-glazes/with-variations")]
    public async Task<ActionResult<RegisterBaseWithVariationsResponse>> RegisterBaseWithVariations(RegisterBaseWithVariationsRequest request, [FromServices] TileTaskService tasks,
        [FromServices] ExperimentService experiments, CancellationToken cancellationToken)
    {
        if ((request.Base is null) == string.IsNullOrWhiteSpace(request.BaseCode))
            throw ApiProblems.Validation("base", "Send either a new base to register or the code of an existing base, not both.");
        var variations = request.Variations ?? [];
        if (request.Assign is not null && variations.Count == 0) throw ApiProblems.Validation("assign", "Add at least one variation to assign.");

        // The in-memory test store has no transactions; everything else runs in one.
        await using var transaction = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(cancellationToken) : null;
        BaseGlaze baseGlaze;
        if (request.Base is { } b)
        {
            AppUser? owner = null;
            if (b.UserId is { } id) owner = await db.Users.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
            else if (!string.IsNullOrWhiteSpace(b.ParticipantCode)) owner = await db.Users.SingleOrDefaultAsync(x => x.ParticipantCode == LabCode.NormalizeParticipant(b.ParticipantCode), cancellationToken);
            if (owner is null) throw new ApiProblemException(400, "participant_not_found", "Participant not found", "Provide the participantCode (e.g. A03) or userId of an existing participant.");
            baseGlaze = await labCodes.RegisterBaseAsync(owner, b.Name, b.Description, b.Formulation ?? [], b.DerivedFromBaseCode, b.ChangeNote, User.GetUserId(), cancellationToken,
                new FiringInput(b.Cone, b.Atmosphere));
        }
        else baseGlaze = await db.BaseGlazes.SingleOrDefaultAsync(x => x.Code == request.BaseCode!.Trim().ToUpperInvariant(), cancellationToken)
            ?? throw new ApiProblemException(404, "base_not_found", "Not found", $"Base {request.BaseCode} does not exist.");

        var recipes = variations.Count == 0 ? [] : await labCodes.RegisterVariationsAsync(baseGlaze, ToInputs(new RegisterVariationsRequest(variations, null)), baseGlaze.OwnerUserId, cancellationToken);
        AssignTileTasksResponse? assigned = null;
        if (request.Assign is { } assign)
            assigned = await tasks.AssignAsync(new AssignTileTasksRequest(recipes.Select(r => r.Id).ToList(), assign.ClayBodies, assign.AssignedUserId, assign.DueOn, assign.Note), User.GetUserId(), cancellationToken);
        ExperimentDto? experiment = null;
        if (request.Experiment is { } x)
            experiment = await experiments.CreateAsync(new CreateExperimentRequest(string.IsNullOrWhiteSpace(x.Name) ? (string.IsNullOrWhiteSpace(baseGlaze.Name) ? null : baseGlaze.Name) : x.Name,
                baseGlaze.Code, x.ParticipantUserId, x.Cups, x.DryGrams, x.WaterGrams, x.TilesPerStep, x.ClayBodies, x.MinReserveGrams, x.Stages), User.GetUserId(), cancellationToken);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);

        return StatusCode(201, new RegisterBaseWithVariationsResponse(await Views.BaseAsync(db, baseGlaze.Id, includeRecipes: false, cancellationToken),
            recipes.Select(r => Views.Recipe(r, baseGlaze.Code)).ToList(), assigned, experiment));
    }

    internal static IReadOnlyList<VariationInput> ToInputs(RegisterVariationsRequest request) =>
        (request.Variations ?? []).Select(v => new VariationInput(v?.Name ?? string.Empty, v?.Description, v?.Additions, new FiringInput(v?.Cone, v?.Atmosphere))).ToList();

    // ---- Capture codes ----

    [HttpGet("capture-codes")]
    public Task<PagedResult<CaptureCodeDto>> CaptureCodes([FromQuery] string? cursor, [FromQuery] int? pageSize, [FromQuery] Guid? assignedUserId, [FromQuery] bool? activeOnly, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var query = db.CaptureCodes.AsNoTracking();
        if (assignedUserId is not null) query = query.Where(x => x.AssignedUserId == assignedUserId);
        if (activeOnly == true) query = query.Usable(now);
        return Paging.PageAsync(query.OrderByDescending(x => x.CreatedAt).ThenBy(x => x.Id), cursor, pageSize, MapCodesAsync, cancellationToken);

        async Task<IReadOnlyList<CaptureCodeDto>> MapCodesAsync(List<CaptureCode> rows)
        {
            var ids = rows.Select(x => x.Id).ToList();
            var userIds = rows.Select(x => x.AssignedUserId).Distinct().ToList();
            var counts = await db.CodeRecipes.Where(x => ids.Contains(x.CaptureCodeId)).GroupBy(x => x.CaptureCodeId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, cancellationToken);
            var names = await db.Users.Where(x => userIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Username, cancellationToken);
            return rows.Select(c => Views.Code(c, names.GetValueOrDefault(c.AssignedUserId), counts.GetValueOrDefault(c.Id))).ToList();
        }
    }

    /// <summary>
    /// Issues a capture code. The plain code is returned once, in this response only; the server keeps a hash and the
    /// last four characters for display.
    /// </summary>
    [HttpPost("capture-codes")]
    public async Task<ActionResult<CaptureCodeDto>> CreateCode(CreateCodeRequest request, CancellationToken cancellationToken)
    {
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == request.AssignedUserId, cancellationToken);
        if (user is null || user.Role != UserRole.User || user.Status != AccountStatus.Active)
            throw new ApiProblemException(400, "user_not_active", "User not active", "Capture codes can only be assigned to an active participant account.");
        string? prefix = null;
        if (!string.IsNullOrWhiteSpace(request.LabelPrefix))
        {
            prefix = request.LabelPrefix.Trim().ToUpperInvariant();
            if (!CaptureCodeGenerator.IsValidLabelPrefix(prefix)) throw ApiProblems.Validation("labelPrefix", "Use up to 32 letters, digits or hyphens, e.g. LAB-A.");
        }
        if (request.InstructionVersionId is not null || request.DefaultTileVariantId is not null)
            throw new ApiProblemException(400, "not_supported", "Not supported", "Instruction versions and default tile variants are not available yet. Leave them empty.");
        if (request.ExpiresAt is { } expires && expires <= DateTimeOffset.UtcNow) throw ApiProblems.Validation("expiresAt", "The expiry must be in the future.");

        var plain = CaptureCodeGenerator.Create(prefix);
        var entity = new CaptureCode
        {
            Id = Guid.CreateVersion7(),
            CodeHash = CaptureCodeGenerator.Hash(plain),
            MaskedSuffix = CaptureCodeGenerator.Suffix(plain),
            LabelPrefix = prefix,
            AssignedUserId = user.Id,
            ExpiresAt = request.ExpiresAt,
            CreatedBy = User.GetUserId()
        };
        db.CaptureCodes.Add(entity);
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Capture code {CodeId} (…{Suffix}) issued to {UserId}", entity.Id, entity.MaskedSuffix, user.Id);
        return Created($"/api/v2/admin/capture-codes/{entity.Id}", Views.Code(entity, user.Username, 0, plain));
    }

    [HttpGet("capture-codes/{codeId:guid}")]
    public async Task<ActionResult<CaptureCodeDto>> GetCode(Guid codeId, CancellationToken cancellationToken)
    {
        var code = await db.CaptureCodes.AsNoTracking().Include(x => x.AssignedUser).SingleOrDefaultAsync(x => x.Id == codeId, cancellationToken) ?? throw ApiProblems.NotFound("Capture code");
        return Ok(Views.Code(code, code.AssignedUser.Username, await db.CodeRecipes.CountAsync(x => x.CaptureCodeId == codeId, cancellationToken)));
    }

    /// <summary>Stops the code accepting new uploads. Uploads already made with it are kept. Idempotent.</summary>
    [HttpPost("capture-codes/{codeId:guid}/revoke")]
    public async Task<IActionResult> RevokeCode(Guid codeId, CancellationToken cancellationToken)
    {
        var code = await db.CaptureCodes.SingleOrDefaultAsync(x => x.Id == codeId, cancellationToken) ?? throw ApiProblems.NotFound("Capture code");
        if (code.RevokedAt is null)
        {
            code.IsActive = false;
            code.RevokedAt = DateTimeOffset.UtcNow;
            code.RevokedBy = User.GetUserId();
            await db.SaveChangesAsync(cancellationToken);
        }
        return NoContent();
    }

    /// <summary>Adds recipes to the code. Recipes already on it stay; repeating an id is harmless.</summary>
    [HttpPost("capture-codes/{codeId:guid}/recipes")]
    public async Task<IActionResult> AssignRecipes(Guid codeId, AssignRecipesRequest request, CancellationToken cancellationToken)
    {
        var code = await db.CaptureCodes.SingleOrDefaultAsync(x => x.Id == codeId, cancellationToken) ?? throw ApiProblems.NotFound("Capture code");
        if (!Views.IsUsable(code, DateTimeOffset.UtcNow)) throw ApiProblems.Conflict("code_not_active", "This capture code is revoked or expired.");
        var recipeIds = (request.RecipeIds ?? []).Distinct().ToArray();
        if (recipeIds.Length == 0) throw ApiProblems.Validation("recipeIds", "Choose at least one recipe.");
        var active = await db.Recipes.Where(x => recipeIds.Contains(x.Id) && x.IsActive).Select(x => x.Id).ToListAsync(cancellationToken);
        if (active.Count != recipeIds.Length) throw ApiProblems.Validation("recipeIds", "One or more recipes do not exist or are not active.");
        var already = await db.CodeRecipes.Where(x => x.CaptureCodeId == codeId).Select(x => x.RecipeId).ToListAsync(cancellationToken);
        db.CodeRecipes.AddRange(active.Except(already).Select(recipeId => new CodeRecipe { CaptureCodeId = codeId, RecipeId = recipeId }));
        await db.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    /// <summary>Recipes on the code, by name. A plain array unless cursor or pageSize is sent, which returns PagedResult.</summary>
    [HttpGet("capture-codes/{codeId:guid}/recipes")]
    [OptInPaging(typeof(RecipeDto))]
    public async Task<IActionResult> CodeRecipes(Guid codeId, [FromQuery] string? cursor, [FromQuery] int? pageSize, CancellationToken cancellationToken)
    {
        if (!await db.CaptureCodes.AnyAsync(x => x.Id == codeId, cancellationToken)) throw ApiProblems.NotFound("Capture code");
        var ordered = db.Recipes.AsNoTracking().Include(x => x.BaseGlaze).Where(x => x.Codes.Any(c => c.CaptureCodeId == codeId)).OrderBy(x => x.Name).ThenBy(x => x.Id);
        if (Paging.Requested(cursor, pageSize))
            return Ok(await Paging.PageAsync(ordered, cursor, pageSize, rows => Task.FromResult<IReadOnlyList<RecipeDto>>(rows.Select(Views.Recipe).ToList()), cancellationToken));
        return Ok((await ordered.ToListAsync(cancellationToken)).Select(Views.Recipe).ToList());
    }

    [HttpDelete("capture-codes/{codeId:guid}/recipes/{recipeId:guid}")]
    public async Task<IActionResult> RemoveCodeRecipe(Guid codeId, Guid recipeId, CancellationToken cancellationToken)
    {
        await db.CodeRecipes.Where(x => x.CaptureCodeId == codeId && x.RecipeId == recipeId).ExecuteDeleteAsync(cancellationToken);
        return NoContent();
    }

    private static string NormalizeRecipeJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return "{}";
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array)) throw ApiProblems.Validation("recipeJson", "recipeJson must be a JSON object or array.");
            return doc.RootElement.GetRawText();
        }
        catch (JsonException) { throw ApiProblems.Validation("recipeJson", "recipeJson is not valid JSON."); }
    }
}
