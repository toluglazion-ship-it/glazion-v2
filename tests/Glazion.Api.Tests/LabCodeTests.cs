using System.Text.Json;
using Glazion.Api.Chemistry;
using Glazion.Api.Contracts;
using Glazion.Api.Data;
using Glazion.Api.Domain;
using Glazion.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Glazion.Api.Tests;

public sealed class LabCodeFormatTests
{
    [Fact]
    public void Formats_participant_base_and_three_digit_recipe_then_four_past_999()
    {
        Assert.Equal("A03-B1", LabCode.Base("A03", 1));
        Assert.Equal("A03-B2-009", LabCode.Recipe("A03", 2, 9));
        Assert.Equal("B02-B12-045", LabCode.Recipe("B02", 12, 45));
        Assert.Equal("A03-B1-999", LabCode.Recipe("A03", 1, 999));
        Assert.Equal("A03-B999-1000", LabCode.Recipe("A03", 999, 1000));
        Assert.Equal("A03-B1-2000", LabCode.Recipe("A03", 1, 2000));
        Assert.Throws<ArgumentOutOfRangeException>(() => LabCode.Recipe("A03", 1, 2001));
        Assert.Throws<ArgumentOutOfRangeException>(() => LabCode.Base("A03", 1000));
        Assert.Equal(["A03-B2-1500", null, null, null],
            new[] { "a03-b2-1500", "A03-B2-0009", "A03-B2-2001", "A03-B1000-001" }.Select(LabCode.NormalizeRecipe));
    }

    [Theory]
    [InlineData("A03", true)]
    [InlineData("Z99", true)]
    [InlineData("a03", false)]
    [InlineData("A3", false)]
    [InlineData("AA03", false)]
    [InlineData("A003", false)]
    public void Validates_participant_codes(string code, bool valid) => Assert.Equal(valid, LabCode.IsValidParticipant(code));

    [Fact]
    public void Parses_recipe_codes_case_insensitively_and_rejects_malformed_ones()
    {
        Assert.True(LabCode.TryParseRecipe("a03-b2-009", out var p, out var b, out var r));
        Assert.Equal(("A03", 2, 9), (p, b, r));
        Assert.Equal("A03-B2-009", LabCode.NormalizeRecipe(" a03-b2-009 "));
        Assert.True(LabCode.TryParseBase("A03-B12", out _, out var baseNumber));
        Assert.Equal(12, baseNumber);
        foreach (var bad in new[] { "A03-B2-9", "A03-B2-0009", "A03-B0-001", "A03-B2-000", "A03-2-001", "A03B2-001", "", "A03-B2-001-x" })
            Assert.False(LabCode.TryParseRecipe(bad, out _, out _, out _), bad);
        Assert.Throws<ArgumentOutOfRangeException>(() => LabCode.Recipe("A03", 1, 2001));
    }
}

public sealed class LabCodeServiceTests
{
    private static GlazionDbContext NewDb() => new(new DbContextOptionsBuilder<GlazionDbContext>().UseInMemoryDatabase("labcodes-" + Guid.NewGuid()).Options);
    private static readonly MaterialRow[] Base = [new("Potash feldspar", 40, "%"), new("Silica", 30, "%"), new("Whiting", 20, "%"), new("Kaolin", 10, "%")];

    private static async Task<AppUser> UserAsync(GlazionDbContext db, string? participant)
    {
        var user = new AppUser { Id = Guid.NewGuid(), Username = "u" + Guid.NewGuid().ToString("N")[..6], ParticipantCode = participant, Role = UserRole.User };
        db.Users.Add(user); await db.SaveChangesAsync();
        return user;
    }

    [Fact]
    public async Task Base_numbers_and_recipe_numbers_are_issued_in_order_with_three_digit_codes()
    {
        await using var db = NewDb();
        var service = new LabCodeService(db);
        var owner = await UserAsync(db, "A03");
        var b1 = await service.RegisterBaseAsync(owner, "Original base", null, Base, null, null, owner.Id, CancellationToken.None);
        var b2 = await service.RegisterBaseAsync(owner, "Base + rutile", null, Base, "A03-B1", "Original base + 2% rutile", owner.Id, CancellationToken.None);
        Assert.Equal(("A03-B1", 1), (b1.Code, b1.BaseNumber));
        Assert.Equal(("A03-B2", 2, b1.Id), (b2.Code, b2.BaseNumber, b2.DerivedFromBaseId));

        var r1 = await service.RegisterVariationAsync(b1, "0.1% cobalt", null, [new("Cobalt carbonate", 0.1m, "%")], owner.Id, CancellationToken.None);
        var r2 = await service.RegisterVariationAsync(b1, "0.2% cobalt", null, [new("Cobalt carbonate", 0.2m, "%")], owner.Id, CancellationToken.None);
        var other = await service.RegisterVariationAsync(b2, "plain", null, [], owner.Id, CancellationToken.None);
        Assert.Equal("A03-B1-001", r1.Code);
        Assert.Equal("A03-B1-002", r2.Code);
        Assert.Equal("A03-B2-001", other.Code);           // numbering is per base
        Assert.Contains("\"baseCode\":\"A03-B1\"", r1.RecipeJson);
        Assert.Contains("Cobalt carbonate", r1.RecipeJson);
    }

    [Fact]
    public async Task Numbers_are_never_reused_after_a_failed_or_deactivated_test()
    {
        await using var db = NewDb();
        var service = new LabCodeService(db);
        var owner = await UserAsync(db, "A03");
        var b1 = await service.RegisterBaseAsync(owner, "Base", null, Base, null, null, owner.Id, CancellationToken.None);
        var r1 = await service.RegisterVariationAsync(b1, "one", null, [], owner.Id, CancellationToken.None);
        r1.IsActive = false; r1.OutcomeNote = "failed: crawled"; await db.SaveChangesAsync();
        var r2 = await service.RegisterVariationAsync(b1, "two", null, [], owner.Id, CancellationToken.None);
        Assert.Equal("A03-B1-002", r2.Code);
        Assert.Equal("A03-B1-001", (await db.Recipes.SingleAsync(x => x.Id == r1.Id)).Code); // still assigned to the failed test
    }

    [Fact]
    public async Task Base_is_full_at_2000_variations()
    {
        await using var db = NewDb();
        var service = new LabCodeService(db);
        var owner = await UserAsync(db, "A03");
        var b1 = await service.RegisterBaseAsync(owner, "Base", null, Base, null, null, owner.Id, CancellationToken.None);
        db.Recipes.Add(new Recipe { Id = Guid.NewGuid(), Name = "999th", BaseGlazeId = b1.Id, RecipeNumber = 999, Code = "A03-B1-999" }); await db.SaveChangesAsync();
        var thousandth = await service.RegisterVariationAsync(b1, "1000th", null, [], owner.Id, CancellationToken.None);
        db.Recipes.Add(new Recipe { Id = Guid.NewGuid(), Name = "last", BaseGlazeId = b1.Id, RecipeNumber = 2000, Code = "A03-B1-2000" }); await db.SaveChangesAsync();
        var ex = await Assert.ThrowsAsync<LabCodeException>(() => service.RegisterVariationAsync(b1, "overflow", null, [], owner.Id, CancellationToken.None));
        Assert.Equal("A03-B1-1000", thousandth.Code);
        Assert.Equal(("base_full", 409), (ex.Code, ex.StatusCode));
    }

    [Fact]
    public async Task A_participant_has_at_most_999_bases()
    {
        await using var db = NewDb();
        var service = new LabCodeService(db);
        var owner = await UserAsync(db, "A03");
        db.BaseGlazes.Add(new BaseGlaze { Id = Guid.NewGuid(), OwnerUserId = owner.Id, ParticipantCode = "A03", BaseNumber = 999, Code = "A03-B999", Name = "last", FormulationJson = "[]" });
        await db.SaveChangesAsync();
        var ex = await Assert.ThrowsAsync<LabCodeException>(() => service.RegisterBaseAsync(owner, "one more", null, Base, null, null, owner.Id, CancellationToken.None));
        Assert.Equal(("participant_full", 409), (ex.Code, ex.StatusCode));
    }

    [Fact]
    public async Task Participant_codes_are_required_unique_and_frozen_once_bases_exist()
    {
        await using var db = NewDb();
        var service = new LabCodeService(db);
        var nobody = await UserAsync(db, null);
        var ex = await Assert.ThrowsAsync<LabCodeException>(() => service.RegisterBaseAsync(nobody, "Base", null, Base, null, null, nobody.Id, CancellationToken.None));
        Assert.Equal("participant_code_missing", ex.Code);

        await service.AssignParticipantCodeAsync(nobody, "a04", CancellationToken.None);
        Assert.Equal("A04", nobody.ParticipantCode);
        await db.SaveChangesAsync();
        var another = await UserAsync(db, null);
        Assert.Equal("participant_code_taken", (await Assert.ThrowsAsync<LabCodeException>(() => service.AssignParticipantCodeAsync(another, "A04", CancellationToken.None))).Code);
        Assert.Equal("invalid_participant_code", (await Assert.ThrowsAsync<LabCodeException>(() => service.AssignParticipantCodeAsync(another, "A4", CancellationToken.None))).Code);

        await service.RegisterBaseAsync(nobody, "Base", null, Base, null, null, nobody.Id, CancellationToken.None);
        Assert.Equal("participant_code_in_use", (await Assert.ThrowsAsync<LabCodeException>(() => service.AssignParticipantCodeAsync(nobody, "A05", CancellationToken.None))).Code);
    }

    [Fact]
    public async Task Derived_base_requires_a_change_note_and_an_existing_parent()
    {
        await using var db = NewDb();
        var service = new LabCodeService(db);
        var owner = await UserAsync(db, "A03");
        await service.RegisterBaseAsync(owner, "Base", null, Base, null, null, owner.Id, CancellationToken.None);
        Assert.Equal("change_note_required", (await Assert.ThrowsAsync<LabCodeException>(() => service.RegisterBaseAsync(owner, "B2", null, Base, "A03-B1", null, owner.Id, CancellationToken.None))).Code);
        Assert.Equal("base_not_found", (await Assert.ThrowsAsync<LabCodeException>(() => service.RegisterBaseAsync(owner, "B2", null, Base, "A03-B9", "x", owner.Id, CancellationToken.None))).Code);
        Assert.Equal("invalid_unit", (await Assert.ThrowsAsync<LabCodeException>(() => service.RegisterBaseAsync(owner, "B2", null, [new("Silica", 1, "cups")], null, null, owner.Id, CancellationToken.None))).Code);
    }

    [Fact]
    public void Combine_sums_shared_materials_and_appends_new_ones()
    {
        var full = LabCodeService.Combine(Base, [new("Silica", 5, "%"), new("Rutile", 2, "%")]);
        Assert.Equal(35, full.Single(x => x.RawName == "Silica").Amount);
        Assert.Equal(2, full.Single(x => x.RawName == "Rutile").Amount);
        Assert.Equal(5, full.Count);
    }
}

public sealed class BaseGlazeEditTests
{
    private static readonly MaterialRow[] Leach = [new("Custer Feldspar", 40, "%"), new("Silica", 30, "%"), new("Vapenec Whiting", 20, "%"), new("Kaolin", 10, "%")];

    private sealed class Fixture : IAsyncDisposable
    {
        public GlazionDbContext Db { get; } = new(new DbContextOptionsBuilder<GlazionDbContext>().UseInMemoryDatabase("base-edit-" + Guid.NewGuid()).Options);
        public AppUser Admin { get; } = new() { Id = Guid.NewGuid(), Username = "admin", Role = UserRole.Admin, Status = AccountStatus.Active };
        public AppUser Ada { get; } = new() { Id = Guid.NewGuid(), Username = "ada", Role = UserRole.User, Status = AccountStatus.Active, ParticipantCode = "A01" };
        public LabCodeService LabCodes { get; }
        public Glazion.Api.Controllers.AdminController Controller { get; }
        public UmfCalculationService Umf { get; }

        public Fixture()
        {
            LabCodes = new LabCodeService(Db);
            Umf = new UmfCalculationService(Db, new ChemistryDataProvider(TestChemistry.Data()), NullLogger<UmfCalculationService>.Instance);
            Controller = new Glazion.Api.Controllers.AdminController(Db, null!, LabCodes, null!, NullLogger<Glazion.Api.Controllers.AdminController>.Instance)
            {
                ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext
                {
                    HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext
                    {
                        User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
                            [new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, Admin.Id.ToString()), new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, "Admin")], "test"))
                    }
                }
            };
        }

        public async Task<(BaseGlaze Base, IReadOnlyList<Recipe> Variations)> InitAsync()
        {
            Db.Users.AddRange(Admin, Ada);
            await Db.SaveChangesAsync();
            var b = await LabCodes.RegisterBaseAsync(Ada, "Leach 4321", "Clear", Leach, null, null, Admin.Id, CancellationToken.None);
            var variations = await LabCodes.RegisterVariationsAsync(b, [new VariationInput("+ 2% ochre", null, [new MaterialRow("Ochre", 2, "%")]), new VariationInput("+ 4% ochre", null, [new MaterialRow("Ochre", 4, "%")])],
                Ada.Id, CancellationToken.None);
            return (b, variations);
        }

        public async Task<UpdateBaseGlazeResponse> EditAsync(string code, UpdateBaseGlazeRequest request) =>
            Unwrap(await Controller.UpdateBaseGlaze(code, request, Umf, CancellationToken.None));

        public async Task<BaseGlazeResponse> GetAsync(string code) => Unwrap(await Controller.BaseGlaze(code, CancellationToken.None));

        private static T Unwrap<T>(Microsoft.AspNetCore.Mvc.ActionResult<T> result) => result.Value ?? (T)((Microsoft.AspNetCore.Mvc.ObjectResult)result.Result!).Value!;

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    private static List<string> FullRows(Recipe r) =>
        JsonDocument.Parse(r.RecipeJson).RootElement.GetProperty("full").EnumerateArray().Select(x => $"{x.GetProperty("rawName").GetString()} {x.GetProperty("amount").GetDecimal()}").ToList();

    [Fact]
    public async Task Before_any_tile_is_made_a_new_formulation_rebuilds_every_variation_and_recalculates_stored_umf()
    {
        await using var f = new Fixture();
        var (b, variations) = await f.InitAsync();
        var before = await f.Umf.CalculateRecipeAsync(variations[0], f.Admin.Id, true, CancellationToken.None); // the first has a stored UMF, the second not
        Assert.True((await f.GetAsync("a01-b1")).FormulationEditable);

        var result = await f.EditAsync("a01-b1", new UpdateBaseGlazeRequest(" Leach 4321 (corrected) ", null,
            [new MaterialRow("Custer Feldspar", 45, "%"), new MaterialRow("Silica", 25, "%"), new MaterialRow("Vapenec Whiting", 20, "%"), new MaterialRow("Kaolin", 10, "%")], null));

        Assert.Equal(("Leach 4321 (corrected)", "Clear", 45m), (result.Base.Name, result.Base.Description, result.Base.Formulation[0].Amount));
        Assert.Equal((2, 1), (result.VariationsRebuilt, result.Recalculated));
        Assert.Empty(result.RecalculationFailed);
        Assert.NotNull(result.Base.UpdatedAt);
        var rebuilt = await f.Db.Recipes.AsNoTracking().Where(r => r.BaseGlazeId == b.Id).OrderBy(r => r.RecipeNumber).ToListAsync();
        Assert.Equal(["Custer Feldspar 45", "Silica 25", "Vapenec Whiting 20", "Kaolin 10", "Ochre 4"], FullRows(rebuilt[1]));
        Assert.Equal("A01-B1", JsonDocument.Parse(rebuilt[0].RecipeJson).RootElement.GetProperty("baseCode").GetString());
        var latest = await f.Db.RecipeCalculationResults.Where(x => x.RecipeId == variations[0].Id).OrderByDescending(x => x.CreatedAt).FirstAsync();
        Assert.NotEqual(before.SnapshotId, latest.Id);                              // a new snapshot from the corrected rows
        Assert.False(await f.Db.RecipeCalculationResults.AnyAsync(x => x.RecipeId == variations[1].Id)); // never calculated: left for later
    }

    [Fact]
    public async Task A_base_goes_to_another_participant_until_stage_1_starts()
    {
        await using var f = new Fixture();
        var (started, _) = await f.InitAsync(); // A01-B1, with variations
        var bea = new AppUser { Id = Guid.NewGuid(), Username = "bea", Role = UserRole.User, Status = AccountStatus.Active, ParticipantCode = "A02" };
        f.Db.Users.Add(bea);
        await f.Db.SaveChangesAsync();
        await f.LabCodes.RegisterBaseAsync(bea, "Bea's own", null, Leach, null, null, f.Admin.Id, CancellationToken.None); // A02-B1
        var fresh = await f.LabCodes.RegisterBaseAsync(f.Ada, "Not started", null, Leach, null, null, f.Admin.Id, CancellationToken.None); // A01-B2
        var experiment = new Experiment { Id = Guid.NewGuid(), BaseGlazeId = fresh.Id, ParticipantUserId = f.Ada.Id, Name = "Cups", CreatedBy = f.Admin.Id };
        f.Db.Experiments.Add(experiment);
        await f.Db.SaveChangesAsync();

        var moved = await f.EditAsync("A01-B2", new UpdateBaseGlazeRequest("Not started yet", null, null, null, ParticipantCode: "a02"));
        Assert.Equal(("A02-B2", "A02", "Not started yet"), (moved.Base.Code, moved.Base.ParticipantCode, moved.Base.Name));
        Assert.Equal(bea.Id, (await f.Db.Experiments.AsNoTracking().SingleAsync(e => e.Id == experiment.Id)).ParticipantUserId);

        // Started (its recipes carry the code): it stays.
        var refused = await Assert.ThrowsAsync<LabCodeException>(() => f.EditAsync(started.Code, new UpdateBaseGlazeRequest(null, null, null, null, ParticipantCode: "A02")));
        Assert.Equal((409, "base_started"), (refused.StatusCode, refused.Code));
        Assert.Equal("participant_not_found", (await Assert.ThrowsAsync<LabCodeException>(() => f.EditAsync("A02-B2", new UpdateBaseGlazeRequest(null, null, null, null, ParticipantCode: "Z99")))).Code);
    }

    [Fact]
    public async Task A_base_in_use_leaves_the_register_keeping_the_variations_with_photos_and_comes_back_on_restore()
    {
        await using var f = new Fixture();
        var (b, variations) = await f.InitAsync();
        var approved = new TileTask { Id = Guid.NewGuid(), RecipeId = variations[0].Id, ClayBody = "porcelain", AssignedUserId = f.Ada.Id, CreatedBy = f.Admin.Id,
            StartedAt = DateTimeOffset.UtcNow, DoneAt = DateTimeOffset.UtcNow, ApprovedAt = DateTimeOffset.UtcNow };
        var open = new TileTask { Id = Guid.NewGuid(), RecipeId = variations[0].Id, ClayBody = "stoneware", AssignedUserId = f.Ada.Id, CreatedBy = f.Admin.Id };
        var other = new TileTask { Id = Guid.NewGuid(), RecipeId = variations[1].Id, ClayBody = "porcelain", AssignedUserId = f.Ada.Id, CreatedBy = f.Admin.Id };
        f.Db.TileTasks.AddRange(approved, open, other);
        await f.Db.SaveChangesAsync();

        Assert.Equal("base_in_use", (await Assert.ThrowsAsync<LabCodeException>(() => f.LabCodes.DeleteBaseAsync(b, f.Admin.Id, CancellationToken.None))).Code);

        // Meeting 2026-11-10: the base goes from the register; the variation in use stays for its photos, its open tile is taken back.
        await f.LabCodes.DeleteBaseAsync(b, f.Admin.Id, CancellationToken.None, keepPhotos: true);
        var kept = await f.Db.Recipes.AsNoTracking().SingleAsync(r => r.Id == variations[0].Id);
        var gone = await f.Db.Recipes.AsNoTracking().SingleAsync(r => r.Id == variations[1].Id);
        Assert.NotNull(b.DeletedAt);
        Assert.Equal((true, (DateTimeOffset?)null), (kept.IsActive, kept.DeletedAt));
        Assert.Equal((false, b.DeletedAt), (gone.IsActive, gone.DeletedAt));
        Assert.Equal(b.DeletedAt, (await f.Db.TileTasks.AsNoTracking().SingleAsync(t => t.Id == open.Id)).CancelledAt);
        Assert.Equal(b.DeletedAt, (await f.Db.TileTasks.AsNoTracking().SingleAsync(t => t.Id == other.Id)).CancelledAt);
        Assert.Equal(b.DeletedAt, (await f.Db.TileTasks.AsNoTracking().SingleAsync(t => t.Id == approved.Id)).CancelledAt); // nothing stays with a participant
        Assert.Equal((true, (DateTimeOffset?)null), (kept.IsActive, kept.DeletedAt)); // and the variation, with its photos, stays

        await f.LabCodes.RestoreBaseAsync(b, f.Admin.Id, CancellationToken.None);
        Assert.Null(b.DeletedAt);
        Assert.True((await f.Db.Recipes.AsNoTracking().SingleAsync(r => r.Id == variations[1].Id)).IsActive);
        Assert.Null((await f.Db.TileTasks.AsNoTracking().SingleAsync(t => t.Id == open.Id)).CancelledAt);
        Assert.Null((await f.Db.TileTasks.AsNoTracking().SingleAsync(t => t.Id == other.Id)).CancelledAt);
        Assert.Null((await f.Db.TileTasks.AsNoTracking().SingleAsync(t => t.Id == approved.Id)).CancelledAt);
    }

    [Fact]
    public async Task Once_a_tile_is_approved_the_formulation_stays_but_name_description_and_note_still_change()
    {
        await using var f = new Fixture();
        var (b, variations) = await f.InitAsync();
        f.Db.TileTasks.Add(new TileTask { Id = Guid.NewGuid(), RecipeId = variations[1].Id, ClayBody = "porcelain", AssignedUserId = f.Ada.Id, CreatedBy = f.Admin.Id,
            StartedAt = DateTimeOffset.UtcNow, DoneAt = DateTimeOffset.UtcNow, ApprovedAt = DateTimeOffset.UtcNow });
        await f.Db.SaveChangesAsync();
        Assert.False((await f.GetAsync("A01-B1")).FormulationEditable);

        var refused = await Assert.ThrowsAsync<LabCodeException>(() => f.EditAsync("A01-B1", new UpdateBaseGlazeRequest(null, null, [new MaterialRow("Silica", 100, "%")], null)));
        Assert.Equal((409, "base_in_use"), (refused.StatusCode, refused.Code));
        Assert.Contains("A01-B1-002", refused.Message);

        // The same rows again (or no rows) is not a change; the other fields go through.
        var renamed = await f.EditAsync("A01-B1", new UpdateBaseGlazeRequest("Leach clear", "Satin in reduction", Leach, "Typo fixed"));
        Assert.Equal(("Leach clear", "Satin in reduction", "Typo fixed", 0), (renamed.Base.Name, renamed.Base.Description, renamed.Base.ChangeNote, renamed.VariationsRebuilt));
        Assert.Equal(["Custer Feldspar 40", "Silica 30", "Vapenec Whiting 20", "Kaolin 10", "Ochre 4"], FullRows(await f.Db.Recipes.AsNoTracking().SingleAsync(r => r.Id == variations[1].Id)));
    }

    [Fact]
    public async Task The_admin_can_change_the_materials_until_a_tile_is_approved_and_the_participant_is_told()
    {
        await using var f = new Fixture();
        var (_, variations) = await f.InitAsync();
        var task = new TileTask { Id = Guid.NewGuid(), RecipeId = variations[0].Id, ClayBody = "porcelain", AssignedUserId = f.Ada.Id, CreatedBy = f.Admin.Id };
        f.Db.TileTasks.Add(task);
        await f.Db.SaveChangesAsync();
        MaterialRow[] changed = [new("Custer Feldspar", 40, "%"), new("Silica", 30, "%"), new("Vapenec Whiting", 25, "%"), new("Kaolin", 5, "%")];

        // Meeting 2026-11-02: started and even made, the admin can still correct it, until they have checked and approved the tile.
        task.StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        task.DoneAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        await f.Db.SaveChangesAsync();
        Assert.True((await f.GetAsync("A01-B1")).FormulationEditable);
        Assert.Equal(2, (await f.EditAsync("A01-B1", new UpdateBaseGlazeRequest(null, null, changed, null))).VariationsRebuilt);
        var changedAt = (await f.Db.Recipes.AsNoTracking().SingleAsync(r => r.Id == variations[0].Id)).MixChangedAt;
        Assert.True(changedAt > task.StartedAt); // the participant who started it is told (TileTaskDto.recipeChangedAt)

        // Checked and approved: the tile is what it is, so the materials stay, even if the task is cancelled later.
        task.ApprovedAt = DateTimeOffset.UtcNow;
        task.CancelledAt = DateTimeOffset.UtcNow;
        await f.Db.SaveChangesAsync();
        var base1 = await f.GetAsync("A01-B1");
        Assert.Equal((false, "A01-B1-001 has been checked and approved"), (base1.FormulationEditable, Assert.Single(base1.FormulationLockedBecause!)));
        var refused = await Assert.ThrowsAsync<LabCodeException>(() => f.EditAsync("A01-B1", new UpdateBaseGlazeRequest(null, null, Leach, null)));
        Assert.Equal("base_in_use", refused.Code);
        Assert.Contains("A01-B1-001 has been checked and approved", refused.Message);
    }

    [Fact]
    public async Task A_variations_colorants_change_until_a_tile_is_approved_and_its_umf_is_recalculated()
    {
        await using var f = new Fixture();
        var (_, variations) = await f.InitAsync();
        await f.Umf.CalculateRecipeAsync(variations[0], f.Admin.Id, true, CancellationToken.None);
        var task = new TileTask { Id = Guid.NewGuid(), RecipeId = variations[0].Id, ClayBody = "porcelain", AssignedUserId = f.Ada.Id, CreatedBy = f.Admin.Id, StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5) };
        f.Db.TileTasks.Add(task);
        await f.Db.SaveChangesAsync();
        var snapshots = await f.Db.RecipeCalculationResults.CountAsync(x => x.RecipeId == variations[0].Id);

        var edited = Unwrap(await f.Controller.UpdateRecipeDetails(variations[0].Id,
            new UpdateRecipeDetailsRequest("+ 3% ochre, 1% silica", null, null, Additions: [new MaterialRow("Ochre", 3, "%"), new MaterialRow("Silica", 1, "%")]), f.Umf, CancellationToken.None));

        Assert.Equal("+ 3% ochre, 1% silica", edited.Name);
        Assert.Equal(["Custer Feldspar 40", "Silica 31", "Vapenec Whiting 20", "Kaolin 10", "Ochre 3"], FullRows(await f.Db.Recipes.AsNoTracking().SingleAsync(r => r.Id == variations[0].Id)));
        Assert.True(edited.MixChangedAt > task.StartedAt);
        Assert.Equal(snapshots + 1, await f.Db.RecipeCalculationResults.CountAsync(x => x.RecipeId == variations[0].Id)); // the stored UMF follows
        Assert.True(Unwrap(await f.Controller.GetRecipe(variations[0].Id, CancellationToken.None)).MixEditable);

        task.ApprovedAt = DateTimeOffset.UtcNow;
        await f.Db.SaveChangesAsync();
        var locked = Unwrap(await f.Controller.GetRecipe(variations[0].Id, CancellationToken.None));
        Assert.Equal((false, "A01-B1-001 has been checked and approved"), (locked.MixEditable, Assert.Single(locked.MixLockedBecause!)));
        Assert.Equal("recipe_in_use", (await Assert.ThrowsAsync<LabCodeException>(() => f.Controller.UpdateRecipeDetails(variations[0].Id,
            new UpdateRecipeDetailsRequest(null, null, null, Additions: [new MaterialRow("Ochre", 5, "%")]), f.Umf, CancellationToken.None))).Code);
    }

    [Fact]
    public async Task Deleting_a_recipe_cancels_its_tiles_keeps_its_number_and_restoring_brings_both_back()
    {
        await using var f = new Fixture();
        var (b, variations) = await f.InitAsync();
        var task = new TileTask { Id = Guid.NewGuid(), RecipeId = variations[1].Id, ClayBody = "porcelain", AssignedUserId = f.Ada.Id, CreatedBy = f.Admin.Id, StartedAt = DateTimeOffset.UtcNow };
        f.Db.TileTasks.Add(task);
        await f.Db.SaveChangesAsync();

        var deleted = Unwrap(await f.Controller.DeleteRecipe(variations[1].Id, CancellationToken.None));
        Assert.NotNull(deleted.DeletedAt);
        Assert.False(deleted.IsActive);
        Assert.NotNull((await f.Db.TileTasks.AsNoTracking().SingleAsync(t => t.Id == task.Id)).CancelledAt);
        Assert.Equal(["A01-B1-001"], (await f.GetAsync("A01-B1")).Recipes!.Select(r => r.Code));
        // The number stays taken: the next variation is 003, not 002.
        Assert.Equal("A01-B1-003", (await f.LabCodes.RegisterVariationAsync(b, "+ 6% ochre", null, [new MaterialRow("Ochre", 6, "%")], f.Ada.Id, CancellationToken.None)).Code);

        var restored = Unwrap(await f.Controller.RestoreRecipe(variations[1].Id, CancellationToken.None));
        Assert.Equal((null, true), (restored.DeletedAt, restored.IsActive));
        var back = await f.Db.TileTasks.AsNoTracking().SingleAsync(t => t.Id == task.Id);
        Assert.Null(back.CancelledAt);
        Assert.NotNull(back.StartedAt); // where it had got to
        Assert.Equal([TileTaskEventKind.Cancelled, TileTaskEventKind.Restored], await f.Db.TileTaskEvents.Where(e => e.TileTaskId == task.Id).OrderBy(e => e.At).Select(e => e.Kind).ToListAsync());

        // Once a tile of it is checked and approved, it stays.
        task.ApprovedAt = DateTimeOffset.UtcNow;
        await f.Db.SaveChangesAsync();
        Assert.Equal("recipe_in_use", (await Assert.ThrowsAsync<LabCodeException>(() => f.Controller.DeleteRecipe(variations[1].Id, CancellationToken.None))).Code);
    }

    [Fact]
    public async Task Deleting_a_base_takes_its_variations_with_it_and_restoring_brings_them_back()
    {
        await using var f = new Fixture();
        var (b, variations) = await f.InitAsync();
        Unwrap(await f.Controller.DeleteRecipe(variations[0].Id, CancellationToken.None)); // deleted on its own first

        var deleted = Unwrap(await f.Controller.DeleteBase("A01-B1", CancellationToken.None));
        Assert.NotNull(deleted.DeletedAt);
        Assert.All(await f.Db.Recipes.AsNoTracking().Where(r => r.BaseGlazeId == b.Id).ToListAsync(), r => Assert.NotNull(r.DeletedAt));
        Assert.Equal("base_deleted", (await Assert.ThrowsAsync<LabCodeException>(() => f.LabCodes.RegisterVariationAsync(b, "x", null, [], f.Ada.Id, CancellationToken.None))).Code);
        Assert.Equal("base_deleted", (await Assert.ThrowsAsync<LabCodeException>(() => f.Controller.RestoreRecipe(variations[1].Id, CancellationToken.None))).Code);

        var restored = Unwrap(await f.Controller.RestoreBase("A01-B1", CancellationToken.None));
        Assert.Null(restored.DeletedAt);
        // Only what was deleted with the base comes back; the variation deleted before stays deleted.
        Assert.Equal(["A01-B1-002"], restored.Recipes!.Select(r => r.Code));
    }

    private static T Unwrap<T>(Microsoft.AspNetCore.Mvc.ActionResult<T> result) => result.Value ?? (T)((Microsoft.AspNetCore.Mvc.ObjectResult)result.Result!).Value!;

    [Fact]
    public async Task A_base_sets_the_firing_its_variations_follow_unless_one_fires_differently()
    {
        await using var f = new Fixture();
        await f.InitAsync();
        var b = await f.LabCodes.RegisterBaseAsync(f.Ada, "Cone 6 clear", null, Leach, null, null, f.Admin.Id, CancellationToken.None, new FiringInput("Cone 6", "Oxidation"));
        var variations = await f.LabCodes.RegisterVariationsAsync(b, [
            new VariationInput("as the base", null, []),
            new VariationInput("same firing, spelt out", null, [], new FiringInput("6", "oxidation")),
            new VariationInput("reduction at 10", null, [], new FiringInput("∆10", "reduction"))], f.Ada.Id, CancellationToken.None);

        Assert.Equal(("6", "oxidation"), (b.Cone, b.Atmosphere));
        Assert.Equal([("6", "oxidation"), ("6", "oxidation"), ("10", "reduction")], variations.Select(v => (v.FiringCone, v.FiringAtmosphere)));
        Assert.Equal([(null, null), (null, null), ("10", "reduction")], variations.Select(v => (v.Cone, v.Atmosphere))); // only a different firing is stored
        Assert.Equal(("6", "oxidation"), ((await f.GetAsync(b.Code)).Cone, (await f.GetAsync(b.Code)).Atmosphere));
        Assert.Equal("validation_error", (await Assert.ThrowsAsync<ApiProblemException>(() => f.LabCodes.RegisterBaseAsync(f.Ada, "x", null, Leach, null, null, f.Admin.Id, CancellationToken.None, new FiringInput("hot", null)))).Code);
        Assert.Equal("validation_error", (await Assert.ThrowsAsync<ApiProblemException>(() => f.LabCodes.RegisterBaseAsync(f.Ada, "x", null, Leach, null, null, f.Admin.Id, CancellationToken.None, new FiringInput(null, "salt")))).Code);

        // A derived base starts from its parent's firing; a cone 06 is kept as written.
        var derived = await f.LabCodes.RegisterBaseAsync(f.Ada, "Lower", null, Leach, b.Code, "Fired lower", f.Admin.Id, CancellationToken.None, new FiringInput("06", null));
        Assert.Equal(("06", "oxidation"), (derived.Cone, derived.Atmosphere));
    }

    [Fact]
    public async Task The_firing_locks_with_the_materials_once_a_tile_is_approved()
    {
        await using var f = new Fixture();
        var (_, variations) = await f.InitAsync();
        var edited = await f.EditAsync("A01-B1", new UpdateBaseGlazeRequest(null, null, null, null, "10", "reduction"));
        Assert.Equal(("10", "reduction"), (edited.Base.Cone, edited.Base.Atmosphere));
        await f.LabCodes.SetRecipeFiringAsync(variations[1], new FiringInput("11", null), CancellationToken.None);
        await f.Db.SaveChangesAsync();
        Assert.Equal(("11", "reduction"), (variations[1].FiringCone, variations[1].FiringAtmosphere));

        f.Db.TileTasks.Add(new TileTask { Id = Guid.NewGuid(), RecipeId = variations[1].Id, ClayBody = "porcelain", AssignedUserId = f.Ada.Id, CreatedBy = f.Admin.Id,
            StartedAt = DateTimeOffset.UtcNow, DoneAt = DateTimeOffset.UtcNow, ApprovedAt = DateTimeOffset.UtcNow });
        await f.Db.SaveChangesAsync();
        Assert.Equal("base_in_use", (await Assert.ThrowsAsync<LabCodeException>(() => f.EditAsync("A01-B1", new UpdateBaseGlazeRequest(null, null, null, null, "6", null)))).Code);
        Assert.Equal("recipe_in_use", (await Assert.ThrowsAsync<LabCodeException>(() => f.LabCodes.SetRecipeFiringAsync(variations[1], new FiringInput("", null), CancellationToken.None))).Code);
        await f.LabCodes.SetRecipeFiringAsync(variations[0], new FiringInput("9", null), CancellationToken.None); // no tile of it approved: still free
        Assert.Equal("9", variations[0].Cone);
    }

    [Fact]
    public async Task Edits_are_validated_and_an_unknown_base_is_not_found()
    {
        await using var f = new Fixture();
        await f.InitAsync();
        Assert.Equal("invalid_name", (await Assert.ThrowsAsync<LabCodeException>(() => f.EditAsync("A01-B1", new UpdateBaseGlazeRequest("  ", null, null, null)))).Code);
        Assert.Equal("formulation_required", (await Assert.ThrowsAsync<LabCodeException>(() => f.EditAsync("A01-B1", new UpdateBaseGlazeRequest(null, null, [], null)))).Code);
        Assert.Equal("invalid_unit", (await Assert.ThrowsAsync<LabCodeException>(() => f.EditAsync("A01-B1", new UpdateBaseGlazeRequest(null, null, [new MaterialRow("Silica", 1, "cups")], null)))).Code);
        Assert.Equal("base_not_found", (await Assert.ThrowsAsync<ApiProblemException>(() => f.EditAsync("A01-B9", new UpdateBaseGlazeRequest("x", null, null, null)))).Code);
    }

    [Fact]
    public async Task A_derived_base_keeps_a_change_note()
    {
        await using var f = new Fixture();
        var (b, _) = await f.InitAsync();
        var derived = await f.LabCodes.RegisterBaseAsync(f.Ada, "Leach + rutile", null, Leach, b.Code, "+ 2% rutile", f.Admin.Id, CancellationToken.None);
        Assert.Equal("change_note_required", (await Assert.ThrowsAsync<LabCodeException>(() => f.EditAsync(derived.Code, new UpdateBaseGlazeRequest(null, null, null, " ")))).Code);
        Assert.Null((await f.EditAsync(b.Code, new UpdateBaseGlazeRequest(null, null, null, ""))).Base.ChangeNote); // an original base may clear it
    }
}
