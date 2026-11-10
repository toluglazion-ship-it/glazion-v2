using System.Text.Json;
using Glazion.Api.Chemistry;
using Glazion.Api.Contracts;
using Glazion.Api.Data;
using Glazion.Api.Domain;
using Glazion.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Glazion.Api.Tests;

/// <summary>Small inline data set. Molecular weights are the ones the calculator doc quotes from oxides.ts; the rest are test-only values.</summary>
internal static class TestChemistry
{
    public static readonly KeyValuePair<string, double>[] Oxides =
    [
        new("SiO2", 60.0843), new("Al2O3", 101.961), new("CaO", 56.077), new("K2O", 94.196), new("Na2O", 61.979), new("MgO", 40.304), new("Fe2O3", 159.687),
        new("B2O3", 69.62), new("ZnO", 81.38), new("PbO", 223.2), new("FeO", 71.84), new("V2O5", 181.88)  // test-only weights
    ];

    public static (string, IReadOnlyList<KeyValuePair<string, JsonElement>>, double?) M(string name, object analysis, double? loi = null) =>
        (name, JsonSerializer.SerializeToElement(analysis).EnumerateObject().Select(p => new KeyValuePair<string, JsonElement>(p.Name, p.Value.Clone())).ToList(), loi);

    /// <summary>The four Leach 4321 materials with the analyses the doc lists, plus edge-case materials.</summary>
    public static ChemistryData Data() => new(Oxides,
    [
        M("Custer Feldspar", new Dictionary<string, double> { ["SiO2"] = 68.5, ["Al2O3"] = 17, ["K2O"] = 10, ["Na2O"] = 3, ["CaO"] = 0.3, ["Fe2O3"] = 0.1 }),
        M("Silica", new Dictionary<string, double> { ["SiO2"] = 100 }),
        M("Vapenec Whiting", new Dictionary<string, double> { ["CaO"] = 55.5, ["MgO"] = 0.4, ["SiO2"] = 0.4 }, 43.6),
        M("Kaolin", new Dictionary<string, double> { ["SiO2"] = 47.29, ["Al2O3"] = 40.21 }, 12.5),
        M("Ochre", new Dictionary<string, double> { ["Fe2O3"] = 55.65, ["SiO2"] = 30 }),
        M("ochre", new Dictionary<string, double> { ["Fe2O3"] = 10.6, ["SiO2"] = 60 }),        // same id, later wins in the arithmetic
        M("   ", new Dictionary<string, double> { ["SiO2"] = 1 }),                              // empty name: dropped
        M("Bare Clay", new Dictionary<string, double>()),                                        // no analysis
        M("Treopax", new Dictionary<string, double> { ["ZrO"] = 93, ["SiO2"] = 5 }),            // unknown symbol
        M("Overfull", new Dictionary<string, double> { ["SiO2"] = 80, ["Al2O3"] = 25 }),        // sums over 100
        M("Earth's Crust", new Dictionary<string, double> { ["SiO2"] = 60, ["B2O3"] = 0, ["CaO"] = 5 }),
        M("Lead Bisilicate", new Dictionary<string, double> { ["PbO"] = 65, ["SiO2"] = 35 }),
        M("Zinc Oxide", new Dictionary<string, double> { ["ZnO"] = 100 })
    ],
    [
        new GlazeLimit("6", "SiO2", 2.5, 3.5, null), new GlazeLimit("6", "Al2O3", 0.25, 0.45, null),
        new GlazeLimit("6", "ZnO", 0.05, 0.2, null), new GlazeLimit("06", "PbO", 0.2, 0.6, "lead")
    ]);

    public static RecipeRowInput Row(string? material, object? amount) => new(material, amount is null ? null : JsonSerializer.SerializeToElement(amount));
    public static readonly RecipeRowInput[] Leach = [Row("Custer Feldspar", 40), Row("Silica", 30), Row("Vapenec Whiting", 20), Row("Kaolin", 10)];
}

public sealed class GlazeChemistryTests
{
    private static readonly ChemistryData Data = TestChemistry.Data();

    [Fact]
    public void Leach_4321_reproduces_the_documented_worked_example()
    {
        var result = GlazeChemistry.CalculateRecipe(TestChemistry.Leach, Data)!;
        var c = result.Chem;
        Assert.Equal(0.26389, c.FluxSum, 5);
        Assert.Equal(3.923, c.Umf["SiO2"], 3); Assert.Equal(0.402, c.Umf["Al2O3"], 3); Assert.Equal(0.758, c.Umf["CaO"], 3);
        Assert.Equal(0.161, c.Umf["K2O"], 3); Assert.Equal(0.073, c.Umf["Na2O"], 3); Assert.Equal(0.008, c.Umf["MgO"], 3); Assert.Equal(0.001, c.Umf["Fe2O3"], 3);
        Assert.Equal(1.03536, c.Moles["SiO2"], 5); Assert.Equal(0.20008, c.Moles["CaO"], 5); Assert.Equal(0.10613, c.Moles["Al2O3"], 5);
        Assert.Equal(69.45, c.AnalysisPct["SiO2"], 2); Assert.Equal(12.53, c.AnalysisPct["CaO"], 2); Assert.Equal(12.08, c.AnalysisPct["Al2O3"], 2);
        Assert.Equal(9.756, c.Ratios.SiAl!.Value, 3);
        Assert.Equal(0.306, c.Ratios.AlkaliOverAlkaline!.Value, 3);
        Assert.Equal(0, c.Ratios.B2O3Pct);
        Assert.Equal(20.10, c.Cte, 2);
        Assert.Empty(c.Warnings);
        Assert.Equal(100, c.MolePct.Values.Sum(), 9);
        Assert.Equal(100, c.AnalysisPct.Values.Sum(), 9);
        Assert.Equal(1, GlazeChemistry.FluxSet.Sum(f => c.Umf.GetValueOrDefault(f)), 12);
        Assert.Equal(["custer-feldspar", "silica", "vapenec-whiting", "kaolin"], result.Lines.Select(l => l.MaterialId));
        Assert.Equal(100, result.Lines.Sum(l => l.PartsPct), 12);
        // Map order is first appearance: line order, then each material's analysis key order.
        Assert.Equal(["SiO2", "Al2O3", "K2O", "Na2O", "CaO", "Fe2O3", "MgO"], c.Umf.Keys);
    }

    [Fact]
    public void Loss_on_ignition_is_not_used()
    {
        var withLoi = GlazeChemistry.CalculateRecipe([TestChemistry.Row("Vapenec Whiting", 50), TestChemistry.Row("Silica", 50)], Data)!;
        // 50 parts × 55.5 % CaO = 27.75 g of CaO; an LOI step would have changed this.
        Assert.Equal(27.75 / 56.077, withLoi.Chem.Moles["CaO"], 12);
    }

    [Fact]
    public void Duplicate_names_take_the_first_by_name_then_the_last_by_id()
    {
        Assert.Equal("ochre", ChemistryData.Slug("Ochre"));
        var r = GlazeChemistry.CalculateRecipe([TestChemistry.Row("OCHRE", 100)], Data)!;
        Assert.Equal(10.6 / 159.687, r.Chem.Moles["Fe2O3"], 12); // the later "ochre" entry's 10.6 %, not 55.65 %
    }

    [Theory]
    [InlineData("Custer Feldspar", "custer-feldspar")]
    [InlineData("  No. 1 Ball Clay ", "no-1-ball-clay")]
    [InlineData("CP-7", "cp-7")]
    [InlineData("CP#7", "cp-7")]
    [InlineData("Earth's Crust", "earth-s-crust")]
    public void Ids_are_slugs_of_the_name(string name, string id) => Assert.Equal(id, ChemistryData.Slug(name));

    [Fact]
    public void Rows_are_skipped_the_way_the_client_skips_them()
    {
        var skipped = new List<SkippedRow>();
        var r = GlazeChemistry.CalculateRecipe(
        [
            TestChemistry.Row(" silica ", "12g"),        // mixed case + spaces, parseFloat("12g") = 12
            TestChemistry.Row("Unobtainium", 10),         // unknown material
            TestChemistry.Row("Kaolin", ""),              // empty amount
            TestChemistry.Row(null, 5),                   // no material
            TestChemistry.Row("Kaolin", "abc"),           // not a number
            TestChemistry.Row("Zinc Oxide", "3.5 parts")
        ], Data, skipped)!;
        Assert.Equal(["silica", "zinc-oxide"], r.Lines.Select(l => l.MaterialId));
        Assert.Equal(12 / 15.5 * 100, r.Lines[0].PartsPct, 12);
        Assert.Equal(["unknown material", "no amount", "no material", "amount is not a number"], skipped.Select(s => s.Reason));
    }

    [Fact]
    public void Nothing_usable_returns_null()
    {
        Assert.Null(GlazeChemistry.CalculateRecipe([TestChemistry.Row("Unobtainium", 10), TestChemistry.Row("Silica", "")], Data));
        Assert.Null(GlazeChemistry.CalculateRecipe([TestChemistry.Row("Silica", 0)], Data));
        Assert.Null(GlazeChemistry.CalculateRecipe([], Data));
        Assert.Null(GlazeChemistry.CalculateRecipe(TestChemistry.Leach, new ChemistryData(TestChemistry.Oxides, [], [])));
    }

    [Fact]
    public void No_flux_gives_an_empty_umf_the_exact_warning_and_no_ratios()
    {
        var c = GlazeChemistry.CalculateRecipe([TestChemistry.Row("Silica", 70), TestChemistry.Row("Kaolin", 30)], Data)!.Chem;
        Assert.Empty(c.Umf);
        Assert.Equal(0, c.FluxSum);
        Assert.Equal(["UMF undefined: flux sum is zero. Add flux oxides."], c.Warnings);
        Assert.Null(c.Ratios.SiAl);
        Assert.Null(c.Ratios.AlkaliOverAlkaline);
        Assert.Equal(0, c.Cte);
        Assert.NotEmpty(c.Moles);
        Assert.NotEmpty(c.AnalysisPct);
    }

    [Fact]
    public void Lead_is_not_a_flux_today_so_lead_bisilicate_and_kaolin_has_no_umf()
    {
        var c = GlazeChemistry.CalculateRecipe([TestChemistry.Row("Lead Bisilicate", 70), TestChemistry.Row("Kaolin", 30)], Data)!.Chem;
        Assert.Empty(c.Umf);
        Assert.Contains(GlazeChemistry.NoFluxWarning, c.Warnings);
    }

    [Fact]
    public void Unknown_oxides_and_oversized_analyses_raise_the_exact_warnings_in_order_with_repeats()
    {
        var c = GlazeChemistry.CalculateRecipe([TestChemistry.Row("Treopax", 10), TestChemistry.Row("Overfull", 10), TestChemistry.Row("Treopax", 5), TestChemistry.Row("Vapenec Whiting", 75)], Data)!.Chem;
        Assert.Equal([
            "Unknown oxide ZrO in material Treopax",
            "Material Overfull oxide sum > 100%",
            "Unknown oxide ZrO in material Treopax"], c.Warnings);
        Assert.DoesNotContain("ZrO", c.Moles.Keys);
        Assert.True(c.Moles["Al2O3"] > 0); // the oversized analysis is still used
    }

    [Fact]
    public void Oxides_listed_at_zero_appear_in_every_map_and_absent_ones_do_not()
    {
        var c = GlazeChemistry.CalculateRecipe([TestChemistry.Row("Earth's Crust", 100)], Data)!.Chem;
        Assert.Equal(0, c.Moles["B2O3"]);
        Assert.Equal(0, c.Umf["B2O3"]);
        Assert.Equal(0, c.AnalysisPct["B2O3"]);
        Assert.False(c.Umf.ContainsKey("Al2O3"));
        Assert.Null(c.Ratios.SiAl); // Al2O3 missing
    }

    [Fact]
    public void Material_with_no_analysis_contributes_nothing_but_still_counts_in_the_scaling()
    {
        var r = GlazeChemistry.CalculateRecipe([TestChemistry.Row("Bare Clay", 50), TestChemistry.Row("Vapenec Whiting", 50)], Data)!;
        Assert.Equal(50, r.Lines[1].PartsPct, 12);
        Assert.Equal(50 * 55.5 / 100 / 56.077, r.Chem.Moles["CaO"], 12);
    }

    [Theory]
    [InlineData("12g", 12)]
    [InlineData("  3.5 ", 3.5)]
    [InlineData(".5", 0.5)]
    [InlineData("-2", -2)]
    [InlineData("1e2x", 100)]
    [InlineData("1e", 1)]
    [InlineData("5.", 5)]
    public void ParseFloat_matches_javascript(string input, double expected) => Assert.Equal(expected, GlazeChemistry.ParseFloat(input));

    [Theory]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData("+")]
    [InlineData("g12")]
    public void ParseFloat_returns_nan_like_javascript(string input) => Assert.True(double.IsNaN(GlazeChemistry.ParseFloat(input)));

    [Fact]
    public void ParseFloat_reads_infinity_which_the_row_rule_then_skips()
    {
        Assert.True(double.IsPositiveInfinity(GlazeChemistry.ParseFloat("Infinity")));
        Assert.Null(GlazeChemistry.CalculateRecipe([TestChemistry.Row("Silica", "Infinity")], Data));
    }

    [Fact]
    public void Limits_suggestions_groups_and_cte_band_follow_the_helpers()
    {
        var r = GlazeChemistry.CalculateRecipe(TestChemistry.Leach, Data)!;
        Assert.Equal(3, GlazeChemistry.LimitsForCone(Data, "6").Count);
        Assert.Single(GlazeChemistry.LimitsForCone(Data, "-6"));               // search API sends 06 as -6
        Assert.Single(GlazeChemistry.LimitsForCone(Data, "06"));
        Assert.Empty(GlazeChemistry.LimitsForCone(Data, "8"));

        var suggestions = GlazeChemistry.SuggestOptimisations(Data, r, "6");
        // SiO2 3.923 is above 2.5–3.5; ZnO is 0 and below 0.05; Al2O3 0.402 is in range.
        Assert.Equal([("SiO2", "decrease"), ("ZnO", "increase")], suggestions.Select(s => (s.Oxide, s.Direction)));
        var silica = suggestions[0];
        Assert.Equal("2.50 - 3.50", silica.Target);
        Assert.Equal(3, silica.Materials.Count);
        Assert.Equal("Silica", silica.Materials[0].Name);                       // highest SiO2 first
        Assert.True(silica.Materials[0].InRecipe);
        Assert.Equal("Zinc Oxide", Assert.Single(suggestions[1].Materials).Name);

        var groups = GlazeChemistry.DisplayGroups(r.Chem);
        Assert.Equal(["CaO", "MgO"], groups.Single(g => g.Key == "ro").Oxides.Select(o => o.Key)); // largest first, zeros hidden
        Assert.Equal("Low CTE: Fits tight, may craze on high-expansion bodies", GlazeChemistry.ClayBodyNote(r.Chem.Cte));
        Assert.Equal("low", GlazeChemistry.CteBand(r.Chem.Cte));
        Assert.Equal("medium", GlazeChemistry.CteBand(65));
        Assert.Equal("high", GlazeChemistry.CteBand(70));
    }

    [Fact]
    public void Calculator_umf_is_accepted_by_the_vector_schema_with_outside_oxides_reported()
    {
        var data = new ChemistryData(TestChemistry.Oxides, [TestChemistry.M("Vanadium Feldspar", new Dictionary<string, double> { ["SiO2"] = 65, ["Al2O3"] = 18, ["K2O"] = 12, ["V2O5"] = 2 })], []);
        var umf = GlazeChemistry.CalculateRecipe([TestChemistry.Row("Vanadium Feldspar", 100)], data)!.Chem.Umf;
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(umf));
        var validation = ChemistrySchema.ValidateUmf(doc.RootElement);
        Assert.True(validation.IsValid, string.Join(",", validation.Errors));
        Assert.Contains("oxide_outside_vector_schema:V2O5", validation.Warnings);
        Assert.DoesNotContain("V2O5", validation.Umf.Keys);
    }

    [Fact]
    public void Ferrous_iron_counts_as_iron_in_the_vector_schema()
    {
        // Iron Oxide Black: FeO beside Fe2O3. Two FeO carry the iron of one Fe2O3.
        using var doc = JsonDocument.Parse("""{"CaO":0.7,"K2O":0.3,"Al2O3":0.4,"SiO2":3.2,"Fe2O3":0.04,"FeO":0.02}""");
        var validation = ChemistrySchema.ValidateUmf(doc.RootElement);
        Assert.True(validation.IsValid, string.Join(",", validation.Errors));
        Assert.Equal(0.05m, validation.Umf["Fe2O3"]);
        Assert.DoesNotContain("FeO", validation.Umf.Keys);
        Assert.Empty(validation.Warnings);

        using var ferrousOnly = JsonDocument.Parse("""{"CaO":1,"SiO2":3,"FeO":0.1}""");
        Assert.Equal(0.05m, ChemistrySchema.ValidateUmf(ferrousOnly.RootElement).Umf["Fe2O3"]);
    }

    [Fact]
    public void Serialised_chem_uses_the_client_field_names()
    {
        var json = JsonSerializer.Serialize(GlazeChemistry.CalculateRecipe(TestChemistry.Leach, Data), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var doc = JsonDocument.Parse(json);
        var chem = doc.RootElement.GetProperty("chem");
        foreach (var key in new[] { "moles", "molePct", "analysisPct", "umf", "fluxSum", "ratios", "cte", "warnings" }) Assert.True(chem.TryGetProperty(key, out _), key);
        var ratios = chem.GetProperty("ratios");
        Assert.True(ratios.TryGetProperty("si_al", out _));
        Assert.True(ratios.TryGetProperty("alkali_over_alkaline", out _));
        Assert.True(ratios.TryGetProperty("b2o3_pct", out _));
        Assert.Equal("custer-feldspar", doc.RootElement.GetProperty("lines")[0].GetProperty("materialId").GetString());

        var noFlux = JsonSerializer.Serialize(GlazeChemistry.CalculateRecipe([TestChemistry.Row("Silica", 1)], Data)!.Chem.Ratios, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal("""{"b2o3_pct":0}""", noFlux);                         // missing ratios are left out
    }
}

/// <summary>Runs only when the client's data files have been synced (tools/sync-chemistry-data.mjs); otherwise reported as skipped.</summary>
public sealed class RequiresChemistryDataFactAttribute : FactAttribute
{
    public static readonly string ApiData = Path.Combine(AppContext.BaseDirectory, "ChemistryData");
    public static readonly string Fixtures = Path.Combine(AppContext.BaseDirectory, "ChemistryData", "fixtures.json");

    public RequiresChemistryDataFactAttribute()
    {
        var missing = new[] { ChemistryData.OxidesFile, ChemistryData.MaterialsFile, ChemistryData.LimitsFile }.Where(f => !File.Exists(Path.Combine(ApiData, f))).ToList();
        if (!File.Exists(Fixtures)) missing.Add("fixtures.json");
        if (missing.Count > 0) Skip = "Client chemistry data not synced (" + string.Join(", ", missing) + "). Run: node tools/sync-chemistry-data.mjs <glazion checkout>";
    }
}

public sealed class ClientFixtureParityTests
{
    [RequiresChemistryDataFact]
    public void Backend_matches_all_client_fixtures_to_ten_decimal_places()
    {
        var data = ChemistryData.Load(RequiresChemistryDataFactAttribute.ApiData);
        Assert.Equal(2830, data.Materials.Count);
        using var doc = JsonDocument.Parse(File.ReadAllText(RequiresChemistryDataFactAttribute.Fixtures));
        var failures = new List<string>();
        foreach (var fixture in doc.RootElement.EnumerateArray())
        {
            var name = fixture.GetProperty("name").GetString();
            var rows = fixture.GetProperty("rows").EnumerateArray().Select(r => new RecipeRowInput(
                r.TryGetProperty("material", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null,
                r.TryGetProperty("amount", out var a) ? a.Clone() : null)).ToList();
            var actual = GlazeChemistry.CalculateRecipe(rows, data)?.Chem;
            var expected = fixture.GetProperty("expected");
            if (expected.ValueKind == JsonValueKind.Null) { if (actual is not null) failures.Add($"{name}: expected null"); continue; }
            if (actual is null) { failures.Add($"{name}: got null"); continue; }

            void Map(string key, OrderedDictionary<string, double> got)
            {
                var want = expected.GetProperty(key).EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetDouble());
                if (!want.Keys.ToHashSet().SetEquals(got.Keys)) { failures.Add($"{name}.{key}: oxides {string.Join(",", got.Keys)} vs {string.Join(",", want.Keys)}"); return; }
                foreach (var (ox, v) in want) if (Math.Abs(v - got[ox]) > 1e-10) failures.Add($"{name}.{key}.{ox}: {got[ox]:R} vs {v:R}");
            }
            Map("moles", actual.Moles); Map("molePct", actual.MolePct); Map("analysisPct", actual.AnalysisPct); Map("umf", actual.Umf);
            if (Math.Abs(expected.GetProperty("fluxSum").GetDouble() - actual.FluxSum) > 1e-10) failures.Add($"{name}.fluxSum");
            if (Math.Abs(expected.GetProperty("cte").GetDouble() - actual.Cte) > 1e-10) failures.Add($"{name}.cte: {actual.Cte:R}");
            var warnings = expected.GetProperty("warnings").EnumerateArray().Select(w => w.GetString()).ToList();
            if (!warnings.SequenceEqual(actual.Warnings)) failures.Add($"{name}.warnings: [{string.Join(" | ", actual.Warnings)}] vs [{string.Join(" | ", warnings)}]");
            var ratios = expected.GetProperty("ratios");
            void Ratio(string key, double? got)
            {
                var has = ratios.TryGetProperty(key, out var w) && w.ValueKind == JsonValueKind.Number;
                if (has != got.HasValue) failures.Add($"{name}.ratios.{key}: presence differs");
                else if (has && Math.Abs(w.GetDouble() - got!.Value) > 1e-10) failures.Add($"{name}.ratios.{key}: {got:R} vs {w.GetDouble():R}");
            }
            Ratio("si_al", actual.Ratios.SiAl); Ratio("alkali_over_alkaline", actual.Ratios.AlkaliOverAlkaline); Ratio("b2o3_pct", actual.Ratios.B2O3Pct);
        }
        Assert.True(failures.Count == 0, $"{failures.Count} mismatches:\n" + string.Join("\n", failures.Take(50)));
        Assert.Equal(40, doc.RootElement.GetArrayLength());
    }
}

public sealed class UmfCalculationServiceTests
{
    private static GlazionDbContext NewDb() => new(new DbContextOptionsBuilder<GlazionDbContext>().UseInMemoryDatabase("umf-" + Guid.NewGuid()).Options);
    private static UmfCalculationService Service(GlazionDbContext db) => new(db, new ChemistryDataProvider(TestChemistry.Data()), NullLogger<UmfCalculationService>.Instance);

    [Fact]
    public void Stateless_calculation_returns_versions_skipped_rows_and_helpers()
    {
        using var db = NewDb();
        var response = Service(db).Calculate(new UmfCalculateRequest([.. TestChemistry.Leach, TestChemistry.Row("Rutile", 2)], "6"));
        Assert.Equal("glazion-umf-v1", response.Versions.CalculationProfile);
        Assert.Equal(3.923, response.Result!.Umf["SiO2"], 3);         // Rutile is skipped exactly as the client skips it…
        var skip = Assert.Single(response.SkippedRows);                // …but the skip is reported, by request index
        Assert.Equal((4, "unknown material"), (skip.Index, skip.Reason));
        Assert.Equal(3, response.Limits.Count);
        Assert.NotEmpty(response.Result.Suggestions);
        Assert.StartsWith("Low CTE", response.Result.ClayBodyNote);
        Assert.Equal("low", response.CteBand);
        Assert.Equal(response.Versions.MaterialsVersion, response.MaterialsVersion);
    }

    [Fact]
    public void Calculator_reports_unavailable_when_data_is_missing()
    {
        using var db = NewDb();
        var provider = new ChemistryDataProvider(Microsoft.Extensions.Options.Options.Create(new ChemistryOptions { DataDirectory = Path.Combine(Path.GetTempPath(), "no-chemistry-" + Guid.NewGuid()) }), NullLogger<ChemistryDataProvider>.Instance);
        var ex = Assert.Throws<ApiProblemException>(() => new UmfCalculationService(db, provider, NullLogger<UmfCalculationService>.Instance).Calculate(new UmfCalculateRequest(TestChemistry.Leach, null)));
        Assert.Equal(("chemistry_data_unavailable", 503), (ex.Code, ex.Status));
    }

    [Fact]
    public async Task Recipe_calculation_persists_one_snapshot_and_repins_waiting_images()
    {
        await using var db = NewDb();
        var owner = Guid.NewGuid();
        var recipe = new Recipe { Id = Guid.CreateVersion7(), Name = "A03-B1-001", Code = "A03-B1-001", OwnerUserId = owner,
            RecipeJson = """{"baseCode":"A03-B1","full":[{"rawName":"Custer Feldspar","amount":40,"unit":"%"},{"rawName":"Silica","amount":30,"unit":"%"},{"rawName":"Vapenec Whiting","amount":20,"unit":"%"},{"rawName":"Kaolin","amount":10,"unit":"%"}]}""" };
        var image = new ImageAsset { Id = Guid.CreateVersion7(), SourceType = ImageSourceType.UserUpload, UploadId = Guid.NewGuid(), OriginalKey = "k", DeclaredContentType = "image/jpeg",
            ApprovalStatus = ApprovalStatus.Approved, QualityStatus = QualityStatus.Passed, ChemistryStatus = ChemistryStatus.Pending, SourceVersion = 3 };
        db.Recipes.Add(recipe); db.ImageAssets.Add(image); db.RecipeImages.Add(new RecipeImage { RecipeId = recipe.Id, ImageId = image.Id, Verified = true });
        await db.SaveChangesAsync();
        var service = Service(db);

        var first = await service.CalculateRecipeAsync(recipe, owner, false, CancellationToken.None);
        Assert.True(first.Created);
        Assert.Equal(1, first.ImagesRepinned);
        var again = await service.CalculateRecipeAsync(recipe, owner, false, CancellationToken.None);
        Assert.False(again.Created);
        Assert.Equal(first.SnapshotId, again.SnapshotId);
        Assert.Equal(first.InputHash, again.InputHash);

        var snapshot = await db.RecipeCalculationResults.SingleAsync();
        Assert.Equal(("glazion-umf-v1", "glazion-chemistry-1.0.0", ChemistrySchema.Version), (snapshot.CalculationProfile, snapshot.CalculationVersion, snapshot.ChemistrySchemaVersion));
        Assert.Contains("\"SiO2\"", snapshot.UmfJson);
        Assert.Contains("custer-feldspar", snapshot.InputJson);
        var pinned = await db.ImageAssets.SingleAsync();
        Assert.Equal((ChemistryStatus.Linked, snapshot.Id, 4L), (pinned.ChemistryStatus, pinned.CalculationResultId, pinned.SourceVersion));
        Assert.Single(await db.ProcessingJobs.Where(j => j.JobType == JobTypes.IndexImageUpsert && j.EntityId == image.Id).ToListAsync());
    }

    [Fact]
    public async Task Recipe_calculation_refuses_unresolved_rows_instead_of_dropping_them()
    {
        await using var db = NewDb();
        var owner = Guid.NewGuid();
        var recipe = new Recipe { Id = Guid.CreateVersion7(), Name = "B2", OwnerUserId = owner,
            RecipeJson = """{"full":[{"rawName":"Silica","amount":30,"unit":"%"},{"rawName":"Ceramic Rutile","amount":2,"unit":"%"},{"rawName":"Custer","amount":40,"unit":"%"}]}""" };
        db.Recipes.Add(recipe); await db.SaveChangesAsync();
        var ex = await Assert.ThrowsAsync<ApiProblemException>(() => Service(db).CalculateRecipeAsync(recipe, owner, false, CancellationToken.None));
        Assert.Equal(("materials_unresolved", 422), (ex.Code, ex.Status));
        var resolutions = (IReadOnlyList<RowResolution>)ex.Extensions["resolutions"]!;
        Assert.Equal(["resolved", "unresolved", "unresolved"], resolutions.Select(r => r.Status));
        Assert.Contains("Custer Feldspar", resolutions[2].Suggestions);
        Assert.Empty(db.RecipeCalculationResults);

        var stranger = await Assert.ThrowsAsync<ApiProblemException>(() => Service(db).CalculateRecipeAsync(recipe, Guid.NewGuid(), false, CancellationToken.None));
        Assert.Equal("recipe_not_found", stranger.Code);
        var mixed = new Recipe { Id = Guid.CreateVersion7(), Name = "m", OwnerUserId = owner, RecipeJson = """{"materials":[{"rawName":"Silica","amount":30,"unit":"g"},{"rawName":"Kaolin","amount":1,"unit":"kg"}]}""" };
        Assert.Equal("mixed_units", (await Assert.ThrowsAsync<ApiProblemException>(() => Service(db).CalculateRecipeAsync(mixed, owner, false, CancellationToken.None))).Code);
    }
}

public sealed class ClientContractTests
{
    [Fact]
    public void Materials_list_has_the_client_material_shape()
    {
        using var db = new GlazionDbContext(new DbContextOptionsBuilder<GlazionDbContext>().UseInMemoryDatabase("mat-" + Guid.NewGuid()).Options);
        var list = new UmfCalculationService(db, new ChemistryDataProvider(TestChemistry.Data()), NullLogger<UmfCalculationService>.Instance).ListMaterials("whiting", 10, 0);
        var json = JsonSerializer.Serialize(list.Items[0], new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("vapenec-whiting", doc.RootElement.GetProperty("id").GetString());
        Assert.Equal("raw", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal(43.6, doc.RootElement.GetProperty("loi_pct").GetDouble());
        Assert.Equal(55.5, doc.RootElement.GetProperty("oxideAnalysis").GetProperty("CaO").GetDouble());
    }

    [Fact]
    public void Saved_recipe_exports_from_the_apps_calculate_on_the_backend()
    {
        // recipeToJson in saved.ts writes materials as { material, amount, group }.
        var recipe = new Recipe { RecipeJson = """{"name":"Leach","cone":"6","materials":[{"material":"Custer Feldspar","amount":40,"group":"base"},{"material":"Silica","amount":30,"group":"base"},{"material":"Vapenec Whiting","amount":20,"group":"base"},{"material":"Kaolin","amount":10,"group":"base"}]}""" };
        var rows = UmfCalculationService.RowsFromRecipe(recipe);
        Assert.Equal(3.923, GlazeChemistry.CalculateRecipe(rows, TestChemistry.Data())!.Chem.Umf["SiO2"], 3);
    }
}
