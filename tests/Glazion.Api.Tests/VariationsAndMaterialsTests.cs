using System.Text.Json;
using Glazion.Api.Chemistry;
using Glazion.Api.Contracts;
using Glazion.Api.Data;
using Glazion.Api.Domain;
using Glazion.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Glazion.Api.Tests;

/// <summary>Base glaze → variation → new base (meeting 2026-10-10), and runs of variations.</summary>
public sealed class VariationPromotionTests
{
    private static GlazionDbContext NewDb() => new(new DbContextOptionsBuilder<GlazionDbContext>().UseInMemoryDatabase("variations-" + Guid.NewGuid()).Options);
    private static readonly MaterialRow[] Base = [new("Nepheline Syenite", 30, "%"), new("Ferro Frit 3134", 20, "%"), new("Silica", 30, "%"), new("EPK", 20, "%")];

    private static async Task<AppUser> UserAsync(GlazionDbContext db, string participant)
    {
        var user = new AppUser { Id = Guid.NewGuid(), Username = "u" + participant, ParticipantCode = participant, Role = UserRole.User };
        db.Users.Add(user); await db.SaveChangesAsync();
        return user;
    }

    [Fact]
    public async Task A_promoted_variation_becomes_the_next_base_with_its_additions_held_constant()
    {
        await using var db = NewDb();
        var service = new LabCodeService(db);
        var p01 = await UserAsync(db, "P01");
        var b1 = await service.RegisterBaseAsync(p01, "Base 1", null, Base, null, null, p01.Id, CancellationToken.None);
        var zircon3 = await service.RegisterVariationAsync(b1, "3% zircon", null, [new("Zircopax", 3, "%")], p01.Id, CancellationToken.None);
        await service.RegisterVariationAsync(b1, "6% zircon", null, [new("Zircopax", 6, "%")], p01.Id, CancellationToken.None);

        var b2 = await service.PromoteVariationAsync(zircon3, p01, null, null, p01.Id, CancellationToken.None);

        Assert.Equal("P01-B2", b2.Code);
        Assert.Equal("3% zircon", b2.Name);
        Assert.Equal(b1.Id, b2.DerivedFromBaseId);
        Assert.Equal(zircon3.Id, b2.PromotedFromRecipeId);
        Assert.Equal("Promoted from P01-B1-001: P01-B1 + 3% Zircopax", b2.ChangeNote);
        var formulation = JsonSerializer.Deserialize<List<MaterialRow>>(b2.FormulationJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(Base.Append(new MaterialRow("Zircopax", 3, "%")), formulation);

        // Variations under the new base start again at 001, and the zircon is part of the base they build on.
        var iron1 = await service.RegisterVariationAsync(b2, "3% zircon + 1% iron", null, [new("Red Iron Oxide", 1, "%")], p01.Id, CancellationToken.None);
        Assert.Equal("P01-B2-001", iron1.Code);
        Assert.Contains("\"Zircopax\"", iron1.RecipeJson);

        var again = await Assert.ThrowsAsync<LabCodeException>(() => service.PromoteVariationAsync(zircon3, p01, null, null, p01.Id, CancellationToken.None));
        Assert.Equal(("already_promoted", 409), (again.Code, again.StatusCode));
        Assert.Contains("P01-B2", again.Message);
    }

    [Fact]
    public async Task Promotion_goes_to_the_named_register_and_refuses_uncoded_or_inactive_recipes()
    {
        await using var db = NewDb();
        var service = new LabCodeService(db);
        var p01 = await UserAsync(db, "P01");
        var a02 = await UserAsync(db, "A02");
        var b1 = await service.RegisterBaseAsync(p01, "Base 1", null, Base, null, null, p01.Id, CancellationToken.None);
        var v = await service.RegisterVariationAsync(b1, "2% shell pink", null, [new("Shell Pink 6000", 2, "%")], p01.Id, CancellationToken.None);

        var inA02 = await service.PromoteVariationAsync(v, a02, "Pink base", "Liked the 2% pink", a02.Id, CancellationToken.None);
        Assert.Equal(("A02-B1", "Pink base", "Liked the 2% pink"), (inA02.Code, inA02.Name, inA02.Description));

        var plain = new Recipe { Id = Guid.NewGuid(), Name = "Admin recipe" };
        Assert.Equal("not_a_variation", (await Assert.ThrowsAsync<LabCodeException>(() => service.PromoteVariationAsync(plain, p01, null, null, p01.Id, CancellationToken.None))).Code);

        var other = await service.RegisterVariationAsync(b1, "5% shell pink", null, [new("Shell Pink 6000", 5, "%")], p01.Id, CancellationToken.None);
        other.IsActive = false;
        Assert.Equal("variation_inactive", (await Assert.ThrowsAsync<LabCodeException>(() => service.PromoteVariationAsync(other, p01, null, null, p01.Id, CancellationToken.None))).Code);
    }

    [Fact]
    public async Task A_run_of_variations_is_numbered_in_order_or_not_at_all()
    {
        await using var db = NewDb();
        var service = new LabCodeService(db);
        var p01 = await UserAsync(db, "P01");
        var b1 = await service.RegisterBaseAsync(p01, "Base 1", null, Base, null, null, p01.Id, CancellationToken.None);
        await service.RegisterVariationAsync(b1, "first", null, [], p01.Id, CancellationToken.None);

        var series = new[] { 2m, 5m, 7.5m, 10m }.Select(pct => new VariationInput($"{pct}% shell pink", null, [new("Shell Pink 6000", pct, "%")])).ToList();
        var created = await service.RegisterVariationsAsync(b1, series, p01.Id, CancellationToken.None);
        Assert.Equal(["P01-B1-002", "P01-B1-003", "P01-B1-004", "P01-B1-005"], created.Select(x => x.Code!).ToArray());
        Assert.Equal("7.5% shell pink", created[2].Name);

        var broken = new List<VariationInput> { new("ok", null, []), new("", null, []) };
        var ex = await Assert.ThrowsAsync<LabCodeException>(() => service.RegisterVariationsAsync(b1, broken, p01.Id, CancellationToken.None));
        Assert.Equal("Variation 2: a name is required (max 180 characters).", ex.Message);
        Assert.Equal(5, await db.Recipes.CountAsync(x => x.BaseGlazeId == b1.Id));

        var tooMany = Enumerable.Range(1, LabCodeService.MaxVariationsPerRequest + 1).Select(i => new VariationInput($"v{i}", null, [])).ToList();
        Assert.Equal("too_many_variations", (await Assert.ThrowsAsync<LabCodeException>(() => service.RegisterVariationsAsync(b1, tooMany, p01.Id, CancellationToken.None))).Code);
    }

    [Fact]
    public async Task A_run_that_would_pass_2000_is_refused_with_the_room_left()
    {
        await using var db = NewDb();
        var service = new LabCodeService(db);
        var p01 = await UserAsync(db, "P01");
        var b1 = await service.RegisterBaseAsync(p01, "Base 1", null, Base, null, null, p01.Id, CancellationToken.None);
        db.Recipes.Add(new Recipe { Id = Guid.NewGuid(), Name = "n", BaseGlazeId = b1.Id, RecipeNumber = 1998, Code = "P01-B1-1998" });
        await db.SaveChangesAsync();

        var three = Enumerable.Range(1, 3).Select(i => new VariationInput($"v{i}", null, [])).ToList();
        var ex = await Assert.ThrowsAsync<LabCodeException>(() => service.RegisterVariationsAsync(b1, three, p01.Id, CancellationToken.None));
        Assert.Equal("P01-B1 has room for 2 more variations, not 3.", ex.Message);
    }

    [Fact]
    public void Additions_are_described_in_the_lab_s_own_words()
    {
        Assert.Equal("2.5% Shell Pink 6000, 1% Red Iron Oxide, 10 g Bentonite",
            LabCodeService.Describe([new("Shell Pink 6000", 2.5m, "%"), new("Red Iron Oxide", 1, "%"), new("Bentonite", 10, "g")]));
    }
}

public sealed class ClayBodyTests
{
    [Theory]
    [InlineData("stoneware", "stoneware")]
    [InlineData(" Porcelain ", "porcelain")]
    [InlineData("Red Body", "red_body")]
    [InlineData("red-body", "red_body")]
    [InlineData("Red", "red_body")]          // what the apps sent before 2026-10-14
    [InlineData(null, null)]
    [InlineData("  ", null)]
    public void Clay_bodies_normalise_to_their_keys(string? input, string? expected)
    {
        Assert.True(new IngestionOptions().TryNormalizeClayBody(input, out var key));
        Assert.Equal(expected, key);
    }

    [Theory]
    [InlineData("earthenware")]
    [InlineData("red clay")]
    public void Unknown_clay_bodies_are_refused(string input) => Assert.False(new IngestionOptions().TryNormalizeClayBody(input, out _));

    [Fact]
    public async Task Backfill_fills_missing_clay_bodies_and_resyncs_approved_images()
    {
        await using var db = new GlazionDbContext(new DbContextOptionsBuilder<GlazionDbContext>().UseInMemoryDatabase("claybody-" + Guid.NewGuid()).Options);
        ImageAsset Asset(ApprovalStatus approval, string? clayBody) => new()
        {
            Id = Guid.CreateVersion7(), SourceType = ImageSourceType.Import, ImportRecordId = Guid.NewGuid(), OriginalKey = Guid.NewGuid().ToString("N"),
            DeclaredContentType = "image/jpeg", ApprovalStatus = approval, ClayBody = clayBody, SourceVersion = 3
        };
        var approved = Asset(ApprovalStatus.Approved, null);
        var pending = Asset(ApprovalStatus.Pending, null);
        var recorded = Asset(ApprovalStatus.Approved, "porcelain");
        db.ImageAssets.AddRange(approved, pending, recorded);
        await db.SaveChangesAsync();
        var ids = new[] { approved.Id, pending.Id, recorded.Id };

        var result = await ClayBodyBackfill.ApplyAsync(db, "stoneware", ids, null, onlyIfMissing: true, CancellationToken.None);
        Assert.Equal(new SetClayBodyResponse("stoneware", 3, 2, 1), result);
        var after = await db.ImageAssets.AsNoTracking().ToDictionaryAsync(x => x.Id);
        Assert.Equal(("stoneware", 4L), (after[approved.Id].ClayBody, after[approved.Id].SourceVersion));
        Assert.Equal("stoneware", after[pending.Id].ClayBody);
        Assert.Equal(("porcelain", 3L), (after[recorded.Id].ClayBody, after[recorded.Id].SourceVersion));
        Assert.Equal([approved.Id], await db.ProcessingJobs.Select(x => x.EntityId).ToListAsync());

        var overwrite = await ClayBodyBackfill.ApplyAsync(db, "stoneware", ids, null, onlyIfMissing: false, CancellationToken.None);
        Assert.Equal(1, overwrite.Updated);
        Assert.Equal("stoneware", (await db.ImageAssets.AsNoTracking().SingleAsync(x => x.Id == recorded.Id)).ClayBody);

        Assert.Equal(400, (await Assert.ThrowsAsync<ApiProblemException>(() => ClayBodyBackfill.ApplyAsync(db, "stoneware", null, null, true, CancellationToken.None))).Status);
    }

    /// <summary>
    /// A participant's tiles: one uploaded through their capture code, one imported for them and linked to their coded
    /// recipe. A Glazy-style import linked to an imported recipe is not theirs.
    /// </summary>
    private static async Task<(GlazionDbContext Db, ImageAsset Uploaded, ImageAsset ImportedForThem, ImageAsset Other, Guid BatchId)> ParticipantFixtureAsync()
    {
        var db = new GlazionDbContext(new DbContextOptionsBuilder<GlazionDbContext>().UseInMemoryDatabase("claybody-p-" + Guid.NewGuid()).Options);
        var p01 = new AppUser { Id = Guid.NewGuid(), Username = "pilot_p01", ParticipantCode = "P01", Role = UserRole.User };
        var code = new CaptureCode { Id = Guid.NewGuid(), AssignedUserId = p01.Id, CodeHash = "h", MaskedSuffix = "ABCD" };
        var batch = new ImportBatch { Id = Guid.NewGuid(), Name = "NO STAIN", CreatedBy = Guid.NewGuid() };
        var otherBatch = new ImportBatch { Id = Guid.NewGuid(), Name = "Glazy corpus", CreatedBy = Guid.NewGuid() };
        var upload = new Upload { Id = Guid.NewGuid(), OwnerUserId = p01.Id, CaptureCodeId = code.Id, ObjectKey = "u", OriginalFileName = "a.jpg", ContentType = "image/jpeg" };
        var record = new ImportRecord { Id = Guid.NewGuid(), BatchId = batch.Id, ExternalRecordId = "r1", ObjectKey = "k1", FileName = "b.jpg", DeclaredContentType = "image/jpeg" };
        var otherRecord = new ImportRecord { Id = Guid.NewGuid(), BatchId = otherBatch.Id, ExternalRecordId = "g1", ObjectKey = "k2", FileName = "c.jpg", DeclaredContentType = "image/jpeg" };
        var coded = new Recipe { Id = Guid.NewGuid(), Name = "pink", Code = "P01-B1-001" };
        var imported = new Recipe { Id = Guid.NewGuid(), Name = "glazy 123", SourceType = RecipeSourceType.Imported };
        ImageAsset Asset(Guid? uploadId, Guid? recordId, ApprovalStatus approval) => new()
        {
            Id = Guid.CreateVersion7(), SourceType = uploadId is null ? ImageSourceType.Import : ImageSourceType.UserUpload, UploadId = uploadId, ImportRecordId = recordId,
            OriginalKey = Guid.NewGuid().ToString("N"), DeclaredContentType = "image/jpeg", ApprovalStatus = approval
        };
        var uploaded = Asset(upload.Id, null, ApprovalStatus.Approved);
        var importedForThem = Asset(null, record.Id, ApprovalStatus.Approved);
        var other = Asset(null, otherRecord.Id, ApprovalStatus.Approved);
        db.AddRange(p01, code, batch, otherBatch, upload, record, otherRecord, coded, imported, uploaded, importedForThem, other);
        db.RecipeImages.AddRange(new RecipeImage { RecipeId = coded.Id, ImageId = importedForThem.Id }, new RecipeImage { RecipeId = imported.Id, ImageId = other.Id });
        await db.SaveChangesAsync();
        return (db, uploaded, importedForThem, other, batch.Id);
    }

    [Fact]
    public async Task A_participant_selection_covers_their_uploads_and_their_coded_recipes_only()
    {
        var (db, uploaded, importedForThem, other, _) = await ParticipantFixtureAsync();
        await using var _db = db;

        var dry = await ClayBodyBackfill.ApplyAsync(db, "porcelain", new ClayBodyBackfill.Selector(ParticipantCode: "p01"), onlyIfMissing: true, dryRun: true, CancellationToken.None);
        Assert.Equal(new SetClayBodyResponse("porcelain", 2, 2, 2, DryRun: true), dry);
        Assert.Empty(db.ProcessingJobs);
        Assert.All(await db.ImageAssets.AsNoTracking().ToListAsync(), a => Assert.Null(a.ClayBody));       // nothing written

        var real = await ClayBodyBackfill.ApplyAsync(db, "porcelain", new ClayBodyBackfill.Selector(ParticipantCode: "P01"), onlyIfMissing: true, dryRun: false, CancellationToken.None);
        Assert.Equal((2, 2), (real.Updated, real.ReindexQueued));
        var after = await db.ImageAssets.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.ClayBody);
        Assert.Equal(("porcelain", "porcelain", (string?)null), (after[uploaded.Id], after[importedForThem.Id], after[other.Id]));
    }

    [Fact]
    public async Task Sources_show_each_batch_and_participant_with_clay_body_counts()
    {
        var (db, _, _, _, batchId) = await ParticipantFixtureAsync();
        await using var _db = db;
        await ClayBodyBackfill.ApplyAsync(db, "porcelain", new ClayBodyBackfill.Selector(ImportBatchId: batchId), true, false, CancellationToken.None);

        var sources = await ClayBodyBackfill.SourcesAsync(db, CancellationToken.None);
        Assert.Contains(sources, s => s.Kind == "importBatch" && s.Label.StartsWith("NO STAIN") && s.Images == 1 && s.WithClayBody == 1);
        Assert.Contains(sources, s => s.Kind == "importBatch" && s.Label.StartsWith("Glazy corpus") && s.Images == 1 && s.WithClayBody == 0);
        Assert.Contains(sources, s => s.Kind == "participant" && s.Key == "P01" && s.Images == 2 && s.WithClayBody == 1);
        Assert.Contains(sources, s => s.Kind == "all" && s.Images == 3 && s.WithClayBody == 1);
    }

    [Theory]
    [InlineData(true, false, null)]
    [InlineData(false, true, null)]
    [InlineData(false, false, "P1")]
    public async Task A_selection_needs_exactly_one_valid_selector(bool ids, bool batch, string? participant)
    {
        await using var db = new GlazionDbContext(new DbContextOptionsBuilder<GlazionDbContext>().UseInMemoryDatabase("claybody-s-" + Guid.NewGuid()).Options);
        var selector = new ClayBodyBackfill.Selector(ids ? [Guid.NewGuid()] : null, batch ? Guid.NewGuid() : null, participant ?? (ids || batch ? "P01" : null));
        Assert.Equal(400, (await Assert.ThrowsAsync<ApiProblemException>(() => ClayBodyBackfill.ApplyAsync(db, "porcelain", selector, true, true, CancellationToken.None))).Status);
    }
}

public sealed class MaterialOverlayTests
{
    private static GlazionDbContext NewDb() => new(new DbContextOptionsBuilder<GlazionDbContext>().UseInMemoryDatabase("materials-" + Guid.NewGuid()).Options);
    private static readonly Guid Admin = Guid.NewGuid();

    private static (MaterialCatalog Catalog, ChemistryDataProvider Provider, UmfCalculationService Umf) Services(GlazionDbContext db)
    {
        var provider = new ChemistryDataProvider(TestChemistry.Data());
        return (new MaterialCatalog(db, provider, NullLogger<MaterialCatalog>.Instance), provider, new UmfCalculationService(db, provider, NullLogger<UmfCalculationService>.Instance));
    }

    [Fact]
    public void An_override_settles_every_duplicate_and_keeps_their_names()
    {
        var file = TestChemistry.Data();
        Assert.Equal(10.6, file.MaterialsById["ochre"].OxideAnalysis.Single(x => x.Key == "Fe2O3").Value.GetDouble()); // the later "ochre" wins today

        var data = ChemistryData.WithOverlay(file, [new MaterialOverlayEntry("ochre", "Ochre", [new("Fe2O3", JsonSerializer.SerializeToElement(40.0)), new("SiO2", JsonSerializer.SerializeToElement(45.0))], 12)]);

        var ochres = data.Materials.Where(m => m.Id == "ochre").ToList();
        Assert.Equal(["Ochre", "ochre"], ochres.Select(m => m.Name).ToArray());
        Assert.All(ochres, m => Assert.Equal(40.0, m.OxideAnalysis.Single(x => x.Key == "Fe2O3").Value.GetDouble()));
        Assert.Equal(MaterialSources.Override, data.SourceOf("ochre"));
        Assert.Equal(MaterialSources.File, data.SourceOf("silica"));
        Assert.StartsWith(file.MaterialsVersion + "+overrides-sha256:", data.MaterialsVersion);
        Assert.Equal(file.Materials.Count, data.Materials.Count);
        Assert.Same(file, ChemistryData.WithOverlay(file, []));
    }

    [Fact]
    public async Task Admin_materials_feed_the_dropdown_and_the_calculator_until_removed()
    {
        await using var db = NewDb();
        var (catalog, provider, umf) = Services(db);
        var before = umf.ListMaterials(null, 500, 0);
        Assert.Single(before.Items, x => x.Id == "ochre");                       // one dropdown entry per id
        Assert.Equal(before.Total, before.Items.Count);

        var added = await catalog.UpsertAsync("frit-3249", new UpsertMaterialRequest("Frit 3249", new Dictionary<string, double> { ["b2o3"] = 33, ["MgO"] = 22, ["SiO2"] = 45 }, null, "Ferro sheet"), Admin, CancellationToken.None);
        Assert.False(added.InFile);
        Assert.Equal(33, added.OxideAnalysis["B2O3"]);                           // symbols take the list's casing
        var listed = umf.ListMaterials("3249", 10, 0).Items.Single();
        Assert.Equal(("frit-3249", MaterialSources.Custom, true), (listed.Id, listed.Source, listed.HasAnalysis));
        Assert.Equal(added.MaterialsVersion, umf.ListMaterials(null, 1, 0).MaterialsVersion);

        var calc = umf.Calculate(new UmfCalculateRequest([TestChemistry.Row("Frit 3249", 50), TestChemistry.Row("Silica", 50)], null));
        Assert.Empty(calc.SkippedRows);

        Assert.True(await catalog.DeleteAsync("frit-3249", Admin, CancellationToken.None));
        Assert.Empty(umf.ListMaterials("3249", 10, 0).Items);
        Assert.Equal(before.MaterialsVersion, umf.ListMaterials(null, 1, 0).MaterialsVersion);
        Assert.False(await catalog.DeleteAsync("frit-3249", Admin, CancellationToken.None));

        // A fresh process reads the table again.
        await catalog.UpsertAsync("ochre", new UpsertMaterialRequest("Ochre", new Dictionary<string, double> { ["Fe2O3"] = 40, ["SiO2"] = 45 }, 12, null), Admin, CancellationToken.None);
        var restarted = new ChemistryDataProvider(TestChemistry.Data());
        Assert.Equal(1, await new MaterialCatalog(db, restarted, NullLogger<MaterialCatalog>.Instance).LoadAsync(CancellationToken.None));
        Assert.True(restarted.TryGet(out var reloaded, out _));
        Assert.Equal(MaterialSources.Override, reloaded.SourceOf("ochre"));
        Assert.True(restarted.TryGetFile(out var file, out _));
        Assert.Equal(MaterialSources.File, file.SourceOf("ochre"));
    }

    [Theory]
    [InlineData("frit-3249", "Frit 3249", "Unobtainium", 10)]
    [InlineData("frit-3249", "Frit 3249", "SiO2", 120)]
    [InlineData("frit-3249", "Frit 3249", "SiO2", -1)]
    [InlineData("frit-3249", "Frit 3134", "SiO2", 50)]
    [InlineData("frit-3249", "", "SiO2", 50)]
    public async Task Bad_material_input_is_refused(string id, string name, string oxide, double value)
    {
        await using var db = NewDb();
        var (catalog, _, _) = Services(db);
        var ex = await Assert.ThrowsAsync<ApiProblemException>(() => catalog.UpsertAsync(id, new UpsertMaterialRequest(name, new Dictionary<string, double> { [oxide] = value }, null, null), Admin, CancellationToken.None));
        Assert.Equal(400, ex.Status);
        Assert.Empty(db.MaterialOverrides);
    }

    [Fact]
    public async Task Duplicates_report_shows_which_entry_the_calculator_uses()
    {
        await using var db = NewDb();
        var (catalog, _, _) = Services(db);
        var group = (await catalog.DuplicatesAsync(CancellationToken.None)).Single();
        Assert.Equal(("ochre", false, false), (group.MaterialId, group.Identical, group.ResolvedByOverride));
        Assert.Equal([false, true], group.Entries.Select(e => e.UsedByCalculator).ToArray());

        await catalog.UpsertAsync("ochre", new UpsertMaterialRequest("Ochre", new Dictionary<string, double> { ["Fe2O3"] = 55.65, ["SiO2"] = 30 }, null, "first entry is right"), Admin, CancellationToken.None);
        Assert.True((await catalog.DuplicatesAsync(CancellationToken.None)).Single().ResolvedByOverride);
    }
}
