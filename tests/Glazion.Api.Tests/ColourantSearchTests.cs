using System.Text.Json;
using Glazion.Api.Chat;
using Glazion.Api.Chemistry;
using Glazion.Api.Contracts;
using Glazion.Api.Data;
using Glazion.Api.Domain;
using Glazion.Api.Indexing;
using Glazion.Api.Infrastructure;
using Glazion.Api.Retrieval;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Glazion.Api.Tests;

public sealed class ColourantTests
{
    internal static ChemistryData Data() => new([.. TestChemistry.Oxides, new("CuO", 79.545), new("Cr2O3", 151.99), new("CoO", 74.93)],
    [
        TestChemistry.M("Custer Feldspar", new Dictionary<string, double> { ["SiO2"] = 68.5, ["Al2O3"] = 17, ["K2O"] = 10, ["Na2O"] = 3, ["CaO"] = 0.3 }),
        TestChemistry.M("Silica", new Dictionary<string, double> { ["SiO2"] = 100 }),
        TestChemistry.M("Whiting", new Dictionary<string, double> { ["CaO"] = 56 }, 44),
        TestChemistry.M("Kaolin", new Dictionary<string, double> { ["SiO2"] = 47.29, ["Al2O3"] = 40.21 }, 12.5),
        TestChemistry.M("Mason Stain 6000", new Dictionary<string, double>()),
        TestChemistry.M("Mason Stain 6001", new Dictionary<string, double>()),
        TestChemistry.M("Iron Oxide Red", new Dictionary<string, double> { ["Fe2O3"] = 100 }),
        TestChemistry.M("Copper Carbonate", new Dictionary<string, double> { ["CuO"] = 71.9 }, 28.1),
        TestChemistry.M("Chrome Oxide", new Dictionary<string, double> { ["Cr2O3"] = 100 }),
        TestChemistry.M("Cobalt Carbonate", new Dictionary<string, double> { ["CoO"] = 63 }, 37),
        // A data gap: a whiting whose analysis is missing. Not a colorant.
        TestChemistry.M("Marblewhite Whiting", new Dictionary<string, double>())
    ], []);

    /// <summary>The shipped catalog's alias for Mason 6000, as potters write it in recipes.</summary>
    internal static readonly MaterialCuration Curation = new([], [new CuratedMaterial("Mason Stain 6000", "stain", ["Shell Pink 6000", "Shell Pink"])]);

    internal static readonly (string Name, double Amount)[] Base = [("Custer Feldspar", 40), ("Silica", 30), ("Whiting", 20), ("Kaolin", 10)];

    internal static IReadOnlyList<RecipeRowInput> Rows(params (string Name, double Amount)[] additions) =>
        Base.Concat(additions).Select(r => TestChemistry.Row(r.Name, r.Amount)).ToList();

    [Fact]
    public void Stains_are_measured_per_hundred_parts_of_the_analysed_base()
    {
        var data = Data();
        var result = GlazeChemistry.CalculateRecipe(Rows(("Mason Stain 6001", 2.5)), data)!;

        var mix = Colourants.FromLines(result.Lines, data, Curation);

        var stain = Assert.Single(mix);
        Assert.Equal(("mason-stain-6001", "Mason Stain 6001", 2.5), (stain.MaterialId, stain.Name, stain.PercentOfBase));
        Assert.Empty(Colourants.FromLines(GlazeChemistry.CalculateRecipe(Rows(), data)!.Lines, data, Curation));
    }

    [Fact]
    public void A_material_with_no_analysis_that_is_not_a_colorant_is_reported_but_not_compared_as_a_stain()
    {
        var data = Data();
        var lines = GlazeChemistry.CalculateRecipe(Rows(("Marblewhite Whiting", 5), ("Mason Stain 6001", 2)), data)!.Lines;

        Assert.Equal("mason-stain-6001", Assert.Single(Colourants.FromLines(lines, data, Curation)).MaterialId);
        Assert.Equal(["Marblewhite Whiting", "Mason Stain 6001"], Colourants.WithoutAnalysis(lines, data).Order());
    }

    [Fact]
    public void A_recipe_row_written_with_an_alias_counts_as_the_material_it_names()
    {
        var data = Data();
        var rows = Rows(("Shell Pink 6000", 5));
        // The calculator alone matches exact names only, so the stain would be skipped.
        Assert.Empty(Colourants.FromLines(GlazeChemistry.CalculateRecipe(rows, data)!.Lines, data, Curation));

        var aliased = UmfCalculationService.WithAliases(rows, data, Curation);

        Assert.Equal("mason-stain-6000", aliased[^1].MaterialId);
        var stain = Assert.Single(Colourants.FromLines(GlazeChemistry.CalculateRecipe(aliased, data)!.Lines, data, Curation));
        Assert.Equal(("mason-stain-6000", 5.0), (stain.MaterialId, stain.PercentOfBase));
        // Exact names and rows that already carry an id are left alone.
        Assert.All(aliased.Take(4), r => Assert.Null(r.MaterialId));
    }

    [Fact]
    public void Stains_do_not_change_the_umf_which_is_why_they_are_compared_separately()
    {
        var data = Data();
        var plain = GlazeChemistry.CalculateRecipe(Rows(), data)!.Chem.Umf;
        var stained = GlazeChemistry.CalculateRecipe(Rows(("Mason Stain 6001", 10)), data)!.Chem.Umf;
        Assert.All(plain, kv => Assert.Equal(kv.Value, stained[kv.Key], 12));
    }

    [Fact]
    public void Colorants_of_both_kinds_are_listed_with_their_amount_on_top_of_the_rest()
    {
        var data = new ChemistryData(TestChemistry.Oxides,
        [
            TestChemistry.M("Custer Feldspar", new Dictionary<string, double> { ["SiO2"] = 68.5, ["Al2O3"] = 17, ["K2O"] = 10, ["Na2O"] = 3, ["CaO"] = 0.3 }),
            TestChemistry.M("Silica", new Dictionary<string, double> { ["SiO2"] = 100 }),
            TestChemistry.M("Whiting", new Dictionary<string, double> { ["CaO"] = 56 }, 44),
            TestChemistry.M("Kaolin", new Dictionary<string, double> { ["SiO2"] = 47.29, ["Al2O3"] = 40.21 }, 12.5),
            TestChemistry.M("Iron Oxide Red", new Dictionary<string, double> { ["Fe2O3"] = 100 }),
            TestChemistry.M("Mason Stain 6001", new Dictionary<string, double>())
        ], []);
        var lines = GlazeChemistry.CalculateRecipe(Rows(("Iron Oxide Red", 3), ("Mason Stain 6001", 2)), data)!.Lines;

        var colorants = Colourants.MaterialsFromLines(lines, data, MaterialCuration.Empty);

        Assert.Equal([("iron-oxide-red", "metallic_oxide", 3.0), ("mason-stain-6001", "stain", 2.0)], colorants.Select(c => (c.MaterialId, c.Kind, c.PercentOfBase)));
        // Only the stain is hidden from the UMF, so only it is compared separately when ranking.
        Assert.Equal("mason-stain-6001", Assert.Single(Colourants.FromLines(lines, data, MaterialCuration.Empty)).MaterialId);
        Assert.Empty(Colourants.MaterialsFromLines(GlazeChemistry.CalculateRecipe(Rows(), data)!.Lines, data, MaterialCuration.Empty));
    }

    private static ColorantProfile Oxides(params (string Oxide, double Umf)[] oxides) =>
        new(oxides.ToDictionary(x => x.Oxide, x => x.Umf), []);

    [Fact]
    public void Colorants_compare_by_kind_and_amount_in_tints()
    {
        var iron = Oxides(("Fe2O3", 0.027));                    // about 1.5% red iron oxide
        Assert.Equal(1, ColorantMatch.Similarity(iron, iron), 9);
        Assert.Equal(1, ColorantMatch.Similarity(Oxides(), Oxides()), 9);

        // A copper green against an iron recipe: no colour in common.
        var copper = Oxides(("CuO", 0.036));                    // about 2% copper oxide
        Assert.Equal(1 / (1.25 + 1.75 + 1), ColorantMatch.Similarity(iron, copper), 9);   // 1.25 and 1.75 tints past the trace
        // The same colorant at a nearby amount stays close; at double, much less so.
        Assert.True(ColorantMatch.Similarity(iron, Oxides(("Fe2O3", 0.024))) > 0.9);
        Assert.Equal(0.6, ColorantMatch.Similarity(iron, Oxides(("Fe2O3", 0.054))), 9);
        // Iron and titanium at the level clays and feldspars carry count as none.
        Assert.True(ColorantMatch.Similarity(Oxides(), Oxides(("Fe2O3", 0.003), ("TiO2", 0.01))) > 0.99);
        // Cobalt is far stronger than iron: a tenth of the amount weighs the same.
        Assert.Equal(ColorantMatch.Similarity(Oxides(), Oxides(("Fe2O3", 0.018))), ColorantMatch.Similarity(Oxides(), Oxides(("CoO", 0.004))), 9);
    }

    [Fact]
    public void Stains_join_the_comparison_only_when_both_sides_know_them()
    {
        var pink = new[] { new Colourant("mason-stain-6000", "Mason Stain 6000", 5) };
        var stained = new ColorantProfile(new Dictionary<string, double>(), pink);
        var plain = new ColorantProfile(new Dictionary<string, double>(), []);
        var unknown = new ColorantProfile(new Dictionary<string, double>(), null);   // a UMF typed by hand

        Assert.True(ColorantMatch.Similarity(plain, stained) < 0.2);
        Assert.Equal(1, ColorantMatch.Similarity(unknown, stained), 9);
        Assert.False(ColorantMatch.ComparesStains(unknown, stained));
        Assert.Equal("5% Mason Stain 6000", ColorantMatch.Describe(stained, withStains: true));
        Assert.Equal("no colorants", ColorantMatch.Describe(stained, withStains: false));
    }

    [Fact]
    public void Colorant_oxides_are_read_from_the_umf_as_stored_with_ferrous_iron_counted_as_iron()
    {
        using var umf = JsonDocument.Parse("""{"CaO":0.7,"K2O":0.3,"SiO2":3.2,"Fe2O3":0.02,"FeO":0.02,"V2O5":0.01,"CoO":0.005}""");
        var oxides = ColorantMatch.OxidesOf(umf.RootElement);
        Assert.Equal(0.03, oxides["Fe2O3"], 9);
        Assert.Equal((0.01, 0.005), (oxides["V2O5"], oxides["CoO"]));
        Assert.False(oxides.ContainsKey("SiO2"));
        Assert.Equal("iron (Fe2O3 0.03), cobalt (CoO 0.005) and vanadium (V2O5 0.01)", ColorantMatch.Describe(new ColorantProfile(oxides, null), withStains: false));
    }

    [Fact]
    public void In_filters_match_any_element_of_a_list_field()
    {
        var metadata = new Dictionary<string, object> { ["colourants"] = new List<string> { "mason-stain-6000", "mason-stain-6001" }, ["tileVariant"] = "white" };
        Assert.True(VectorFilter.AnyOf("colourants", ["mason-stain-6001"]).Matches(metadata));
        Assert.False(VectorFilter.AnyOf("colourants", ["mason-stain-6020"]).Matches(metadata));
        Assert.True(VectorFilter.AnyOf("tileVariant", ["white"]).Matches(metadata));
    }

    [Fact]
    public void Pinecone_routes_chemistry_and_text_to_their_own_indexes()
    {
        var options = Options.Create(new VectorIndexOptions { Provider = "Pinecone", Pinecone = new PineconeOptions
        {
            ApiKey = "k", ChemistryIndexHost = "glazion-image-chemistry-abc.svc.aped-4627-b74a.pinecone.io", TextIndexHost = "https://glazion-image-text-def.svc.aped-4627-b74a.pinecone.io/"
        } });
        var index = new PineconeVectorIndex(new HttpClient(), options, NullLogger<PineconeVectorIndex>.Instance);

        Assert.Equal("https://glazion-image-chemistry-abc.svc.aped-4627-b74a.pinecone.io/", index.HostFor(VectorNamespaces.ImageChemistry("oxide-vector-v1")));
        Assert.Equal("https://glazion-image-text-def.svc.aped-4627-b74a.pinecone.io/", index.HostFor(VectorNamespaces.ImageText("text-embedding-3-small:2026-09")));
    }

    [Fact]
    public void Knowledge_reads_every_configured_namespace_and_index_once()
    {
        var chat = new ChatOptions { OpenAI = new ChatModelOptions { ApiKey = "sk" }, Knowledge = new KnowledgeOptions
        {
            Provider = "Pinecone", PineconeApiKey = "pc", PineconeIndexHost = "glazeai-2-vegi1sw.svc.aped-4627-b74a.pinecone.io",
            Namespaces = "glazes, firing ,", Sources = [new KnowledgeIndexSource { IndexHost = "https://more-knowledge.svc.pinecone.io", Namespace = "studio", EmbeddingModel = "text-embedding-3-large" },
                new KnowledgeIndexSource { IndexHost = "glazeai-2-vegi1sw.svc.aped-4627-b74a.pinecone.io", Namespace = "glazes" }]
        } };
        var source = new PineconeKnowledgeSource(new HttpClient(), Options.Create(chat), NullLogger<PineconeKnowledgeSource>.Instance);

        Assert.True(source.IsConfigured);
        Assert.Equal(
        [
            ("https://glazeai-2-vegi1sw.svc.aped-4627-b74a.pinecone.io/", "", "text-embedding-3-small"),
            ("https://glazeai-2-vegi1sw.svc.aped-4627-b74a.pinecone.io/", "glazes", "text-embedding-3-small"),
            ("https://glazeai-2-vegi1sw.svc.aped-4627-b74a.pinecone.io/", "firing", "text-embedding-3-small"),
            ("https://more-knowledge.svc.pinecone.io/", "studio", "text-embedding-3-large")
        ], source.Targets());
    }
}

public sealed class ColourantSearchTests : IAsyncDisposable
{
    private readonly GlazionDbContext _db = new(new DbContextOptionsBuilder<GlazionDbContext>().UseInMemoryDatabase("colourant-search-" + Guid.NewGuid()).Options);
    private readonly InMemoryVectorIndex _index = new();
    private readonly HashingTextEmbeddingProvider _embeddings = new(Options.Create(new EmbeddingOptions()));
    private readonly ChemistryData _data = ColourantTests.Data();
    private readonly ChemistryDataProvider _chemistry;
    private readonly Guid _userId = Guid.NewGuid();

    public ColourantSearchTests() => _chemistry = new ChemistryDataProvider(_data, ColourantTests.Curation);

    private RecipeImageSearchService Service() => new(_db, _index, _embeddings, new NoStorage(), Options.Create(new RetrievalOptions()), Options.Create(new IngestionOptions()), _chemistry, NullLogger<RecipeImageSearchService>.Instance);

    private async Task<Recipe> RecipeAsync(string name, params (string Name, double Amount)[] additions)
    {
        var rows = ColourantTests.Base.Concat(additions).Select(r => new { rawName = r.Name, amount = r.Amount, unit = "%" }).ToList();
        var recipe = new Recipe { Id = Guid.CreateVersion7(), Name = name, SourceType = RecipeSourceType.Imported, RecipeJson = JsonSerializer.Serialize(new { full = rows }) };
        var result = GlazeChemistry.CalculateRecipe(UmfCalculationService.RowsFromRecipe(recipe), _data)!;
        var snapshot = new RecipeCalculationResult
        {
            Id = Guid.CreateVersion7(), RecipeId = recipe.Id, CalculationProfile = GlazeChemistry.CalculationProfile, CalculationVersion = GlazeChemistry.CalculationVersion,
            MaterialAnalysisVersion = "test", ChemistrySchemaVersion = ChemistrySchema.Version, InputHash = Guid.NewGuid().ToString("N"),
            UmfJson = JsonSerializer.Serialize(result.Chem.Umf), InputJson = JsonSerializer.Serialize(new { lines = result.Lines }, new JsonSerializerOptions(JsonSerializerDefaults.Web))
        };
        _db.Recipes.Add(recipe);
        _db.RecipeCalculationResults.Add(snapshot);
        await _db.SaveChangesAsync();
        _snapshots[recipe.Id] = snapshot.Id;
        return recipe;
    }

    private readonly Dictionary<Guid, Guid> _snapshots = new();

    private async Task<ImageAsset> TileAsync(Recipe recipe)
    {
        var asset = new ImageAsset
        {
            Id = Guid.CreateVersion7(), SourceType = ImageSourceType.Import, ImportRecordId = Guid.NewGuid(), OriginalKey = $"prod/originals/default/imports/b/{Guid.NewGuid():N}/source.jpg",
            DeclaredContentType = "image/jpeg", DeclaredSizeBytes = 1, TileVariantKey = "white", ConeMin = 6, ConeMax = 6, Atmosphere = "oxidation",
            QualityStatus = QualityStatus.Passed, ApprovalStatus = ApprovalStatus.Approved, SourceVersion = 1,
            ChemistryStatus = ChemistryStatus.Linked, CalculationResultId = _snapshots[recipe.Id]
        };
        _db.ImageAssets.Add(asset);
        _db.RecipeImages.Add(new RecipeImage { RecipeId = recipe.Id, ImageId = asset.Id, Verified = true });
        await _db.SaveChangesAsync();
        Assert.Equal(JobResultKind.Succeeded, (await new ImageIndexer(_db, _index, _embeddings, NullLogger<ImageIndexer>.Instance, _chemistry).SyncAsync(asset.Id, CancellationToken.None)).Kind);
        return asset;
    }

    private static RecipeImageSearchRequest Materials(params (string Name, double Amount)[] additions) =>
        new(new SearchQueryInput("raw_recipe", null, null, null, null, null, null, null,
                ColourantTests.Base.Concat(additions).Select(r => new RawMaterialInput(r.Name, (decimal)r.Amount, "%")).ToList()),
            new SearchFiringInput(6, 6, "oxidation", false), null, 5, false);

    [Fact]
    public async Task A_materials_search_finds_the_tile_with_the_same_stain_among_hundreds_with_the_same_umf()
    {
        _db.Users.Add(new AppUser { Id = _userId, Username = "potter", Role = UserRole.User });
        // 60 tiles of a different stain on the same base: identical UMF, so the plain chemistry lookup (50 candidates)
        // cannot tell them from the one we want.
        var shellPink = await RecipeAsync("Shell Pink 5%", ("Mason Stain 6000", 5));
        for (var i = 0; i < 60; i++) await TileAsync(shellPink);
        var rose25 = await RecipeAsync("Alpine Rose 2.5%", ("Mason Stain 6001", 2.5));
        var target = await TileAsync(rose25);
        var rose5 = await RecipeAsync("Alpine Rose 5%", ("Mason Stain 6001", 5));
        var rose5Tile = await TileAsync(rose5);
        var plain = await RecipeAsync("Base");
        var plainTile = await TileAsync(plain);

        var response = await Service().SearchAsync(Materials(("Mason Stain 6001", 2.5)), new SearchRequester(_userId, false), null, CancellationToken.None);

        // The materials were broken down into UMF, and the stain the UMF cannot see is reported.
        Assert.NotNull(response.Query.Umf);
        Assert.True(response.Query.Umf!["SiO2"] > 0);
        var stain = Assert.Single(response.Query.Colourants!);
        Assert.Equal(("mason-stain-6001", 2.5), (stain.MaterialId, stain.PercentOfBase));
        // Shown to the person as a colorant of the stain kind, with its amount.
        Assert.Equal(("stain", 2.5), (Assert.Single(response.Query.ColorantMaterials!).Kind, response.Query.ColorantMaterials![0].PercentOfBase));

        // Closest match: the tile with that exact stain and amount.
        Assert.Equal(target.Id, response.ClosestMatch!.ImageId);
        Assert.Equal(response.Items[0], response.ClosestMatch);
        Assert.Equal(1.0, response.ClosestMatch.ScoreBreakdown.ColourantSimilarity);
        Assert.Contains("colourants", response.ClosestMatch.MatchedFields);

        // Similar: one image per other recipe, best first, never the closest match's recipe.
        var similar = response.Similar!.Select(x => x.ImageId).ToList();
        Assert.Equal(rose5Tile.Id, similar[0]);
        Assert.Contains(plainTile.Id, similar);
        Assert.Single(similar, id => _db.RecipeImages.Any(r => r.ImageId == id && r.RecipeId == shellPink.Id));
        Assert.DoesNotContain(target.Id, similar);
        // 2.5% against 5% of the same stain: 2.25 and 4.75 tints past the trace, with one of slack.
        Assert.Equal(3.25 / 5.75, response.Similar![0].ScoreBreakdown.ColourantSimilarity!.Value, 4);
        Assert.Contains(response.Similar![0].Limitations, l => l == "Colorants differ: you searched 2.5% Mason Stain 6001; this tile has 5% Mason Stain 6001.");
        Assert.Contains("no_oxide_analysis:Mason Stain 6001", response.Warnings);

        // A saved search comes back with the same closest match, suggestions and chemistry.
        var again = await Service().GetAsync(response.SearchId, new SearchRequester(_userId, false), CancellationToken.None);
        Assert.Equal(target.Id, again!.ClosestMatch!.ImageId);
        Assert.Equal(similar, again.Similar!.Select(x => x.ImageId));
        Assert.Equal(2.5, again.Query.Colourants!.Single().PercentOfBase);
        Assert.Equal("stain", again.Query.ColorantMaterials!.Single().Kind);
        // Feedback is accepted on a suggestion too.
        await Service().RecordFeedbackAsync(new SearchFeedbackRequest(response.SearchId, similar[0], "relevant", null), new SearchRequester(_userId, false), CancellationToken.None);
    }

    [Fact]
    public async Task A_little_iron_ranks_iron_tiles_above_copper_and_chrome_greens_of_the_same_base()
    {
        // The 27 Oct meeting's screenshot: a recipe with a little iron and no stain, where green and teal tiles of the
        // same base scored 84%. Their UMFs differ only in 0.02–0.05 of colorant beside about 3 of silica.
        _db.Users.Add(new AppUser { Id = _userId, Username = "potter", Role = UserRole.User });
        var iron = await TileAsync(await RecipeAsync("Base + 1.5% iron", ("Iron Oxide Red", 1.5)));
        var moreIron = await TileAsync(await RecipeAsync("Base + 2% iron", ("Iron Oxide Red", 2)));
        var copper = await TileAsync(await RecipeAsync("Base + 3% copper carbonate", ("Copper Carbonate", 3)));
        var chrome = await TileAsync(await RecipeAsync("Base + 0.5% chrome", ("Chrome Oxide", 0.5)));
        var plain = await TileAsync(await RecipeAsync("Base"));

        var response = await Service().SearchAsync(Materials(("Iron Oxide Red", 1.5)), new SearchRequester(_userId, false), null, CancellationToken.None);

        var ranked = response.Items.Select(x => x.ImageId).ToList();
        Assert.Equal([iron.Id, moreIron.Id, plain.Id], ranked.Take(3));
        Assert.Equal([copper.Id, chrome.Id], ranked.Skip(3).Order());
        var green = response.Items.Single(x => x.ImageId == copper.Id);
        Assert.True(green.ScoreBreakdown.ColourantSimilarity < 0.3, $"{green.ScoreBreakdown.ColourantSimilarity}");
        Assert.NotEqual("high", green.ConfidenceBand);
        Assert.Contains(green.Limitations, l => l.StartsWith("Colorants differ: you searched iron (Fe2O3 0.", StringComparison.Ordinal) && l.Contains("; this tile has copper (CuO 0.", StringComparison.Ordinal));
        Assert.Contains("colourants", response.ClosestMatch!.MatchedFields);

        // A UMF typed by hand has no stains to compare, but its metallic oxides count just the same.
        var umf = JsonSerializer.Deserialize<JsonElement>((await _db.RecipeCalculationResults.SingleAsync(x => x.Id == iron.CalculationResultId)).UmfJson);
        var byUmf = await Service().SearchAsync(new RecipeImageSearchRequest(new SearchQueryInput("umf", umf, null, "glazion-umf-v1", null, null, null, null, null), new SearchFiringInput(6, 6, "oxidation", false), null, 5, false),
            new SearchRequester(_userId, false), null, CancellationToken.None);
        Assert.Equal(iron.Id, byUmf.ClosestMatch!.ImageId);
        Assert.True(byUmf.Items.Single(x => x.ImageId == copper.Id).ScoreBreakdown.ColourantSimilarity < 0.3);
    }

    [Fact]
    public async Task A_tile_recipe_that_names_a_stain_by_its_alias_is_compared_as_that_stain()
    {
        _db.Users.Add(new AppUser { Id = _userId, Username = "potter", Role = UserRole.User });
        // Imported recipe rows say "Shell Pink 6000"; the material is Mason Stain 6000. No snapshot lines, so the
        // tile's stains come from its recipe rows.
        var recipe = await RecipeAsync("Shell Pink 5%", ("Shell Pink 6000", 5));
        var snapshot = await _db.RecipeCalculationResults.SingleAsync(x => x.RecipeId == recipe.Id);
        snapshot.InputJson = "{}";
        await _db.SaveChangesAsync();
        var tile = await TileAsync(recipe);

        var response = await Service().SearchAsync(Materials(("Mason Stain 6000", 5)), new SearchRequester(_userId, false), null, CancellationToken.None);

        Assert.Equal(tile.Id, response.ClosestMatch!.ImageId);
        Assert.Equal(1.0, response.ClosestMatch.ScoreBreakdown.ColourantSimilarity);
    }

    [Fact]
    public async Task Match_check_shows_what_was_typed_beside_each_match_and_where_the_tile_came_from()
    {
        _db.Users.Add(new AppUser { Id = _userId, Username = "potter", DisplayName = "Pat Potter", Role = UserRole.User });
        var rose25 = await RecipeAsync("Alpine Rose 2.5%", ("Mason Stain 6001", 2.5));
        var target = await TileAsync(rose25);
        var plain = await RecipeAsync("Base");
        var plainTile = await TileAsync(plain);
        var search = await Service().SearchAsync(Materials(("Mason Stain 6001", 2.5)), new SearchRequester(_userId, false), null, CancellationToken.None);
        await Service().RecordFeedbackAsync(new SearchFeedbackRequest(search.SearchId, plainTile.Id, "not_relevant", "No pink at all"), new SearchRequester(_userId, false), CancellationToken.None);
        var matchCheck = new MatchCheckService(_db, new NoStorage(), _chemistry);

        var detail = (await matchCheck.GetAsync(search.SearchId, CancellationToken.None))!;

        // The rows as typed, each with its kind, so a stain or metallic oxide stands out.
        Assert.True(detail.Query.MaterialsAsTyped);
        Assert.Equal(["Custer Feldspar", "Silica", "Whiting", "Kaolin", "Mason Stain 6001"], detail.Query.Materials!.Select(m => m.Name));
        Assert.Equal((2.5m, "%", "stain"), (detail.Query.Materials![4].Amount, detail.Query.Materials[4].Unit, detail.Query.Materials[4].Kind));
        Assert.Equal("Pat Potter", detail.Requester!.DisplayName);

        // The closest match, traced to its recipe and the chemistry it was compared on.
        var closest = detail.Results[0];
        Assert.Equal(("closest", target.Id), (closest.Shown, closest.Image!.ImageId));
        var recipe = Assert.Single(closest.Image.Recipes);
        Assert.Equal("Alpine Rose 2.5%", recipe.Name);
        Assert.Contains(recipe.Materials, m => m.Name == "Mason Stain 6001" && m.Amount == 2.5m && m.Kind == "stain");
        Assert.True(closest.Image.Chemistry!.ComparedAtSearch);
        Assert.Equal(2.5, Assert.Single(closest.Image.Colourants!).PercentOfBase);
        // Same base, so every oxide agrees.
        Assert.Contains(closest.Comparison, c => c.Oxide == "SiO2" && c.Group == "glass" && c.Difference == 0);
        Assert.All(closest.Comparison, c => Assert.Equal(0, c.Difference));

        // The plain tile was suggested too, with the person's feedback on it.
        var other = Assert.Single(detail.Results, r => r.Image?.ImageId == plainTile.Id);
        Assert.Equal(("not_relevant", "No pink at all"), (other.Feedback, other.FeedbackNote));

        // The list: newest first, with what was typed and the top tile; the not-relevant filter keeps it.
        var list = await matchCheck.ListAsync(null, null, false, null, null, null, CancellationToken.None);
        var row = Assert.Single(list.Items);
        Assert.Equal((search.SearchId, "raw_recipe", target.Id, 1), (row.Id, row.QueryType, row.Top!.ImageId, row.NotRelevant));
        Assert.Contains("Mason Stain 6001", row.Materials);
        Assert.Single((await matchCheck.ListAsync(null, null, true, "pat", null, null, CancellationToken.None)).Items);
        Assert.Empty((await matchCheck.ListAsync(null, "umf", false, null, null, null, CancellationToken.None)).Items);

        // From the photo library (no search), the tile's current chemistry.
        var trace = (await matchCheck.TraceAsync([target.Id], null, CancellationToken.None))[target.Id];
        Assert.Null(trace.Chemistry!.ComparedAtSearch);
        Assert.Equal(ImageSourceType.Import, trace.Source);
    }

    [Fact]
    public void Match_check_compares_oxides_in_schema_order_and_groups_colourants()
    {
        var rows = MatchCheckService.Compare(
            new Dictionary<string, decimal> { ["SiO2"] = 3m, ["CaO"] = 0.6m, ["K2O"] = 0.4m, ["Fe2O3"] = 0.02m },
            new Dictionary<string, decimal> { ["SiO2"] = 3.2m, ["CaO"] = 0.6m, ["K2O"] = 0.4m, ["CoO"] = 0.05m });

        Assert.Equal(["K2O", "CaO", "SiO2", "Fe2O3", "CoO"], rows.Select(r => r.Oxide));
        Assert.Equal(("colourant", 0.05m, 0m, 0.05m), (rows[4].Group, rows[4].Tile, rows[4].Searched, rows[4].Difference));
        Assert.Equal(-0.02m, rows[3].Difference);
        Assert.Equal(0.2m, rows[2].Difference);
        Assert.All(MatchCheckService.Compare(new Dictionary<string, decimal> { ["SiO2"] = 3m }, null), r => Assert.Null(r.Difference));
    }

    [Fact]
    public async Task Match_check_lists_each_tile_beside_its_recipe_and_chemistry()
    {
        var rose = await TileAsync(await RecipeAsync("Alpine Rose 2.5%", ("Mason Stain 6001", 2.5)));
        var plain = await TileAsync(await RecipeAsync("Base"));
        var controller = new Glazion.Api.Controllers.AdminImagesController(_db, new NoStorage(), null!, Options.Create(new IngestionOptions()),
            new MatchCheckService(_db, new NoStorage(), _chemistry), null!);
        async Task<PagedResult<ImageTrace>> List(string? q = null, ImageSourceType? source = null, int? pageSize = null, string? cursor = null) =>
            (await controller.Traces(null, null, source, null, null, null, null, null, q, cursor, pageSize, CancellationToken.None)).Value!;

        var page = await List(pageSize: 1);
        var newest = Assert.Single(page.Items);                                      // newest first, one page at a time
        Assert.Equal((plain.Id, 2), (newest.ImageId, page.TotalCount));
        var next = await List(pageSize: 1, cursor: page.NextCursor);
        var traced = Assert.Single(next.Items);
        Assert.Equal(rose.Id, traced.ImageId);
        Assert.Contains(traced.Recipes[0].Materials, m => m.Name == "Mason Stain 6001" && m.Kind == "stain");
        Assert.NotNull(traced.Chemistry);
        Assert.Equal(rose.Id, Assert.Single((await List(q: "rose")).Items).ImageId);
        Assert.Empty((await List(source: ImageSourceType.UserUpload)).Items);
    }

    [Fact]
    public async Task Index_documents_carry_the_tiles_stains_for_filtering()
    {
        var rose = await RecipeAsync("Alpine Rose 2.5%", ("Mason Stain 6001", 2.5));
        var tile = await TileAsync(rose);
        var record = await _db.IndexRecords.FirstAsync(x => x.EntityId == tile.Id);
        Assert.Contains("mason-stain-6001", record.MetadataJson);
        Assert.Equal("InMemory", record.VectorProvider);
    }

    public ValueTask DisposeAsync() => _db.DisposeAsync();

    private sealed class NoStorage : IR2ObjectStorage
    {
        public Task<PresignedUpload> CreatePutUrlAsync(string k, string t, IReadOnlyDictionary<string, string>? m, CancellationToken ct) => throw new NotSupportedException();
        public Task<Uri> CreateGetUrlAsync(string k, CancellationToken ct) => CreateGetUrlAsync(k, null, ct);
        public Task<Uri> CreateGetUrlAsync(string k, string? n, CancellationToken ct) => Task.FromResult(new Uri($"https://signed.example/{k}"));
        public Task<StoredObject?> HeadAsync(string k, CancellationToken ct) => throw new NotSupportedException();
        public Task<Stream> OpenReadAsync(string k, CancellationToken ct) => throw new NotSupportedException();
        public Task CopyAsync(string s, string d, CancellationToken ct) => throw new NotSupportedException();
        public Task DeleteAsync(string k, CancellationToken ct) => throw new NotSupportedException();
        public Task PutAsync(string k, byte[] c, string t, CancellationToken ct) => throw new NotSupportedException();
    }
}
