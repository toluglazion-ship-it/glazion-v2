using Glazion.Api.Contracts;
using System.Text.Json;
using Glazion.Api.Chemistry;
using Glazion.Api.Data;
using Glazion.Api.Domain;
using Glazion.Api.Indexing;
using Glazion.Api.Infrastructure;
using Glazion.Api.Retrieval;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Glazion.Api.Tests;

public sealed class HybridRankerTests
{
    private static readonly Dictionary<string, decimal> Umf = new() { ["Na2O"] = 0.18m, ["K2O"] = 0.12m, ["CaO"] = 0.52m, ["MgO"] = 0.18m, ["Al2O3"] = 0.35m, ["B2O3"] = 0.20m, ["SiO2"] = 3.20m };

    [Theory]
    [InlineData(6.0, 6.0, "oxidation", 6.0, 6.0, "oxidation", 1.00, ConeMatch.Exact, AtmosphereMatch.Exact)]
    [InlineData(6.0, 6.0, "oxidation", 5.0, 6.0, "oxidation", 0.85, ConeMatch.Compatible, AtmosphereMatch.Exact)]
    [InlineData(6.0, 6.0, "oxidation", 7.0, 7.0, "oxidation", 0.85, ConeMatch.Compatible, AtmosphereMatch.Exact)]   // within ±1 cone
    [InlineData(6.0, 6.0, "oxidation", 6.0, 6.0, null, 0.60, ConeMatch.Exact, AtmosphereMatch.Unknown)]
    [InlineData(6.0, 6.0, "oxidation", 6.0, 6.0, "reduction", 0.30, ConeMatch.Exact, AtmosphereMatch.Conflict)]
    [InlineData(6.0, 6.0, "oxidation", 10.0, 10.0, "oxidation", 0.30, ConeMatch.Conflict, AtmosphereMatch.Exact)]
    [InlineData(6.0, 6.0, "oxidation", null, null, "oxidation", 0.60, ConeMatch.Unknown, AtmosphereMatch.Exact)]
    [InlineData(6.0, 6.0, "oxidation", null, null, null, 0.50, ConeMatch.Unknown, AtmosphereMatch.Unknown)]
    public void Firing_compatibility_follows_the_plan_table(double qMin, double qMax, string? qAtm, double? cMin, double? cMax, string? cAtm, double expected, ConeMatch cone, AtmosphereMatch atm)
    {
        var result = HybridRanker.Firing(new FiringQuery((decimal)qMin, (decimal)qMax, qAtm), (decimal?)cMin, (decimal?)cMax, cAtm, 1m);
        Assert.Equal(expected, result.Score!.Value, 6);
        Assert.Equal((cone, atm), (result.Cone, result.Atmosphere));
    }

    [Fact]
    public void Firing_is_unavailable_when_the_query_gives_none_and_cone_only_queries_ignore_atmosphere()
    {
        Assert.Null(HybridRanker.Firing(new FiringQuery(null, null, null), 6, 6, "oxidation", 1m).Score);
        Assert.Equal(1.0, HybridRanker.Firing(new FiringQuery(6, 6, null), 6, 6, "reduction", 1m).Score!.Value);
        Assert.Equal(0.6, HybridRanker.Firing(new FiringQuery(null, null, "oxidation"), 6, 6, null, 1m).Score!.Value);
    }

    [Fact]
    public void Identical_chemistry_scores_one_and_magnitude_changes_are_caught_by_the_distance_term()
    {
        var same = HybridRanker.CompareChemistry(Umf, Umf, 0.5);
        Assert.Equal(1.0, same.Similarity, 6);
        Assert.Equal(0.0, same.Distance, 6);
        Assert.Equal(1.0, same.Coverage);

        // Doubling silica keeps cosine high (silica dominates direction) but the weighted distance exposes it.
        var moreSilica = new Dictionary<string, decimal>(Umf) { ["SiO2"] = 6.4m };
        var cmp = HybridRanker.CompareChemistry(Umf, moreSilica, 0.5);
        Assert.True(cmp.Cosine > 0.95);
        Assert.True(cmp.DistanceSimilarity < 0.5);
        Assert.True(cmp.Similarity < same.Similarity);
        Assert.Null(cmp.SilicaDelta is <= 0.25m ? cmp.SilicaDelta : null);
    }

    [Fact]
    public void Coverage_reports_query_oxides_the_candidate_does_not_measure()
    {
        var candidate = Umf.Where(x => x.Key is not ("B2O3" or "MgO")).ToDictionary();
        var cmp = HybridRanker.CompareChemistry(Umf, candidate, 0.5);
        Assert.Equal(5.0 / 7, cmp.Coverage, 6);
        Assert.Equal(["MgO", "B2O3"], cmp.MissingQueryOxides);
    }

    [Fact]
    public void Unavailable_dimensions_are_renormalised_away()
    {
        var w = new RankerWeights();
        Assert.Equal((0.55 * 0.9 + 0.20 * 1.0 + 0.15 * 0.7) / 0.90, HybridRanker.Combine(w, 0.9, 1.0, 0.7, null), 9);
        Assert.Equal(0.8, HybridRanker.Combine(w, 0.8, null, null, null), 9);
        Assert.Equal(0.0, HybridRanker.Combine(w, null, null, null, null));
    }

    [Fact]
    public void Confidence_band_drops_one_level_per_missing_modality()
    {
        var o = new RetrievalOptions();
        Assert.Equal("high", HybridRanker.Band(0.85, 0, o));
        Assert.Equal("medium", HybridRanker.Band(0.85, 1, o));
        Assert.Equal("low", HybridRanker.Band(0.85, 2, o));
        Assert.Equal("medium", HybridRanker.Band(0.65, 0, o));
        Assert.Equal("low", HybridRanker.Band(0.50, 3, o));
    }
}

public sealed class RecipeImageSearchServiceTests
{
    private static readonly string BlueUmf = """{"Na2O":0.18,"K2O":0.12,"CaO":0.52,"MgO":0.18,"Al2O3":0.35,"B2O3":0.2,"SiO2":3.2}""";
    private static readonly string MatteUmf = """{"K2O":0.25,"CaO":0.45,"MgO":0.30,"Al2O3":0.50,"SiO2":2.2}""";

    private sealed class FakeStorage : IR2ObjectStorage
    {
        public Task<PresignedUpload> CreatePutUrlAsync(string objectKey, string contentType, IReadOnlyDictionary<string, string>? metadata, CancellationToken ct) => throw new NotSupportedException();
        public Task<Uri> CreateGetUrlAsync(string objectKey, CancellationToken ct) => CreateGetUrlAsync(objectKey, null, ct);
        public Task<Uri> CreateGetUrlAsync(string objectKey, string? name, CancellationToken ct) => Task.FromResult(new Uri($"https://signed.example/{objectKey}?name={name}"));
        public Task<StoredObject?> HeadAsync(string objectKey, CancellationToken ct) => throw new NotSupportedException();
        public Task<Stream> OpenReadAsync(string objectKey, CancellationToken ct) => throw new NotSupportedException();
        public Task CopyAsync(string s, string d, CancellationToken ct) => throw new NotSupportedException();
        public Task DeleteAsync(string k, CancellationToken ct) => throw new NotSupportedException();
        public Task PutAsync(string k, byte[] c, string t, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public GlazionDbContext Db { get; } = new(new DbContextOptionsBuilder<GlazionDbContext>().UseInMemoryDatabase("search-" + Guid.NewGuid()).Options);
        public InMemoryVectorIndex Index { get; } = new();
        public HashingTextEmbeddingProvider Embeddings { get; } = new(Options.Create(new EmbeddingOptions()));
        public AppUser Admin { get; } = new() { Id = Guid.NewGuid(), Username = "admin", Role = UserRole.Admin };
        public AppUser User { get; } = new() { Id = Guid.NewGuid(), Username = "potter", Role = UserRole.User, ParticipantCode = "A03" };
        public RetrievalOptions Retrieval { get; } = new();

        public RecipeImageSearchService Service() => new(Db, Index, Embeddings, new FakeStorage(), Options.Create(Retrieval), Options.Create(new IngestionOptions()), new ChemistryDataProvider(TestChemistry.Data()), NullLogger<RecipeImageSearchService>.Instance);
        public SearchRequester AsUser => new(User.Id, false);
        /// <summary>The admin setting (on by default): every search with a cone keeps only tiles within ±1 cone.</summary>
        public Task StrictFiringAsync(bool on) => SearchSettings.UpdateAsync(Db, new UpdateSearchSettingsRequest(on), Admin.Id, CancellationToken.None);

        public async Task InitAsync() { Db.Users.AddRange(Admin, User); await Db.SaveChangesAsync(); }

        public async Task<ImageAsset> AddImageAsync(string? umfJson, string variant = "white", decimal? cone = 6, string? atmosphere = "oxidation", string labels = """{"surface":"glossy","colourFamily":"blue"}""", bool index = true, string? recipeCode = null, string? clayBody = null)
        {
            var asset = new ImageAsset
            {
                Id = Guid.CreateVersion7(), SourceType = ImageSourceType.Import, ImportRecordId = Guid.NewGuid(), OriginalKey = $"prod/originals/default/imports/b/{Guid.NewGuid():N}/source.jpg",
                DeclaredContentType = "image/jpeg", DeclaredSizeBytes = 1, TileVariantKey = variant, ConeMin = cone, ConeMax = cone, Atmosphere = atmosphere, LabelsJson = labels, ClayBody = clayBody,
                QualityStatus = QualityStatus.Passed, ApprovalStatus = ApprovalStatus.Approved, SourceVersion = 2, ChemistryStatus = ChemistryStatus.Missing
            };
            if (umfJson is not null)
            {
                var recipe = new Recipe { Id = Guid.CreateVersion7(), Name = "r", SourceType = RecipeSourceType.Imported, Code = recipeCode };
                var snapshot = new RecipeCalculationResult { Id = Guid.CreateVersion7(), RecipeId = recipe.Id, CalculationProfile = ChemistrySchema.ProvidedUmfProfile, CalculationVersion = "import-1.0.0", MaterialAnalysisVersion = "n/a", ChemistrySchemaVersion = ChemistrySchema.Version, InputHash = Guid.NewGuid().ToString("N"), UmfJson = umfJson };
                Db.Recipes.Add(recipe); Db.RecipeCalculationResults.Add(snapshot);
                Db.RecipeImages.Add(new RecipeImage { RecipeId = recipe.Id, ImageId = asset.Id, Verified = true });
                asset.ChemistryStatus = ChemistryStatus.Linked; asset.CalculationResultId = snapshot.Id;
            }
            Db.ImageAssets.Add(asset);
            await Db.SaveChangesAsync();
            if (index) Assert.Equal(JobResultKind.Succeeded, (await new ImageIndexer(Db, Index, Embeddings, NullLogger<ImageIndexer>.Instance).SyncAsync(asset.Id, CancellationToken.None)).Kind);
            return asset;
        }

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    private static RecipeImageSearchRequest UmfQuery(string umf, SearchFiringInput? firing = null, SearchFiltersInput? filters = null, int? topK = 12) =>
        new(new SearchQueryInput("umf", JsonDocument.Parse(umf).RootElement, null, "provided-umf-v1", null, null, null, null, null),
            firing ?? new SearchFiringInput(6, 6, "oxidation", false), filters, topK, false);

    [Fact]
    public async Task Closest_chemistry_and_firing_ranks_first_with_explanations_and_persisted_run()
    {
        await using var f = new Fixture(); await f.InitAsync();
        var blue = await f.AddImageAsync(BlueUmf);
        var matte = await f.AddImageAsync(MatteUmf, cone: 10, atmosphere: "reduction", labels: """{"surface":"matte","colourFamily":"brown"}""");
        await f.StrictFiringAsync(false); // ranks the far-fired tile below instead of hiding it

        var response = await f.Service().SearchAsync(UmfQuery(BlueUmf, filters: new SearchFiltersInput(null, ["glossy"], null, null)), f.AsUser, null, CancellationToken.None);

        var first = response.Items[0];
        Assert.Equal(blue.Id, first.ImageId);
        Assert.Equal(1, first.Rank);
        Assert.Equal("real_tile", first.ResultType);
        Assert.Equal("high", first.ConfidenceBand);
        Assert.Equal(1.0, first.ScoreBreakdown.ChemistrySimilarity);
        Assert.Equal(1.0, first.ScoreBreakdown.FiringCompatibility);
        Assert.Null(first.ScoreBreakdown.ImageSimilarity);
        Assert.Contains("cone", first.MatchedFields); Assert.Contains("atmosphere", first.MatchedFields); Assert.Contains("silica", first.MatchedFields); Assert.Contains("surface", first.MatchedFields);
        Assert.Contains("Exact firing schedule was not recorded.", first.Limitations);
        Assert.Equal("image_only", first.Source.MetadataVisibility);
        Assert.Null(first.Source.RecipeName);
        Assert.NotNull(first.DisplayUrl);
        Assert.True(response.Items.All(i => i.ImageId != matte.Id) || response.Items.Single(i => i.ImageId == matte.Id).Score < first.Score);
        Assert.StartsWith("sha256:", response.Query.InputHash);
        Assert.Equal("hybrid-ranker-1.2.0", response.Ranker.Version);
        Assert.Equal(2, response.Ranker.CandidateCount);

        var run = await f.Db.SearchRuns.SingleAsync(x => x.Id == response.SearchId);
        Assert.Equal(("umf", f.User.Id, response.Items.Count), (run.QueryType, run.RequesterUserId, run.ResultCount));
        Assert.Contains("hashing-256:v1", run.QuerySnapshotJson);
        Assert.Contains("oxide-vector-v1", run.QuerySnapshotJson);
        Assert.NotNull(run.LatencyMs);
    }

    [Fact]
    public async Task Unapproved_rejected_and_stale_vectors_never_appear()
    {
        await using var f = new Fixture(); await f.InitAsync();
        var good = await f.AddImageAsync(BlueUmf);
        var rejectedLater = await f.AddImageAsync(BlueUmf);
        rejectedLater.ApprovalStatus = ApprovalStatus.Rejected; await f.Db.SaveChangesAsync();          // vector still in the index
        var superseded = await f.AddImageAsync(BlueUmf);
        foreach (var r in f.Db.IndexRecords.Where(x => x.EntityId == superseded.Id)) r.SupersededAt = DateTimeOffset.UtcNow;
        await f.Db.SaveChangesAsync();                                                                       // approved, but no live manifest row

        var response = await f.Service().SearchAsync(UmfQuery(BlueUmf), f.AsUser, null, CancellationToken.None);
        Assert.Equal([good.Id], response.Items.Select(x => x.ImageId).ToArray());
        Assert.Equal(3, response.Ranker.CandidateCount);
    }

    [Fact]
    public async Task Clay_body_filter_keeps_only_tiles_recorded_on_that_body()
    {
        await using var f = new Fixture(); await f.InitAsync();
        var stoneware = await f.AddImageAsync(BlueUmf, clayBody: "stoneware");
        var porcelain = await f.AddImageAsync(BlueUmf, clayBody: "porcelain");
        var unknown = await f.AddImageAsync(BlueUmf);
        var service = f.Service();

        var filtered = await service.SearchAsync(UmfQuery(BlueUmf, filters: new SearchFiltersInput(null, null, null, null, ["Stoneware"])), f.AsUser, null, CancellationToken.None);
        Assert.Equal([stoneware.Id], filtered.Items.Select(x => x.ImageId).ToArray());
        Assert.Equal("stoneware", filtered.Items[0].Tile.ClayBody);
        Assert.Contains("clayBody", filtered.Items[0].MatchedFields);
        Assert.Contains("\"clayBodies\":[\"stoneware\"]", (await f.Db.SearchRuns.SingleAsync(x => x.Id == filtered.SearchId)).FiltersJson);

        var all = await service.SearchAsync(UmfQuery(BlueUmf), f.AsUser, null, CancellationToken.None);
        Assert.Equal(3, all.Items.Count);
        Assert.Contains("Clay body was not recorded for this tile.", all.Items.Single(x => x.ImageId == unknown.Id).Limitations);
        Assert.DoesNotContain("Clay body was not recorded for this tile.", all.Items.Single(x => x.ImageId == porcelain.Id).Limitations);

        var bad = await Assert.ThrowsAsync<ApiProblemException>(() => service.SearchAsync(UmfQuery(BlueUmf, filters: new SearchFiltersInput(null, null, null, null, ["earthenware"])), f.AsUser, null, CancellationToken.None));
        Assert.Equal(400, bad.Status);
    }

    [Fact]
    public async Task Hard_filters_apply_tile_variant_and_strict_firing()
    {
        await using var f = new Fixture(); await f.InitAsync();
        var white6 = await f.AddImageAsync(BlueUmf, variant: "white", cone: 6);
        var pink6 = await f.AddImageAsync(BlueUmf, variant: "pink", cone: 6);
        var white10 = await f.AddImageAsync(BlueUmf, variant: "white", cone: 10);
        var whiteNoCone = await f.AddImageAsync(BlueUmf, variant: "white", cone: null);
        var service = f.Service();

        var variant = await service.SearchAsync(UmfQuery(BlueUmf, filters: new SearchFiltersInput(["white"], null, null, null)), f.AsUser, null, CancellationToken.None);
        Assert.DoesNotContain(variant.Items, x => x.ImageId == pink6.Id);

        var strict = await service.SearchAsync(UmfQuery(BlueUmf, firing: new SearchFiringInput(5, 7, "oxidation", true), filters: new SearchFiltersInput(["white"], null, null, null)), f.AsUser, null, CancellationToken.None);
        Assert.Equal([white6.Id], strict.Items.Select(x => x.ImageId).ToArray());

        // With strict firing turned off and without the variant filter, the white and pink cone-6 tiles match chemistry and
        // firing exactly and outrank the rest; they differ only in text similarity (their canonical text names different tile variants).
        await f.StrictFiringAsync(false);
        var soft = await service.SearchAsync(UmfQuery(BlueUmf, firing: new SearchFiringInput(6, 6, "oxidation", false)), f.AsUser, null, CancellationToken.None);
        Assert.Equal(new[] { white6.Id, pink6.Id }.OrderBy(x => x), soft.Items.Take(2).Select(x => x.ImageId).OrderBy(x => x));
        Assert.All(soft.Items.Take(2), x => { Assert.Equal(1.0, x.ScoreBreakdown.ChemistrySimilarity); Assert.Equal(1.0, x.ScoreBreakdown.FiringCompatibility); });
        var noCone = soft.Items.Single(x => x.ImageId == whiteNoCone.Id);
        Assert.Contains("Firing cone was not recorded for this tile.", noCone.Limitations);
        var outOfRange = soft.Items.Single(x => x.ImageId == white10.Id);
        Assert.Equal(0.30, outOfRange.ScoreBreakdown.FiringCompatibility);
        Assert.True(outOfRange.Score < soft.Items[0].Score);
    }

    [Fact]
    public async Task Firing_is_strict_by_default_within_one_cone_and_an_admin_can_turn_it_off()
    {
        await using var f = new Fixture(); await f.InitAsync();
        var six = await f.AddImageAsync(BlueUmf, cone: 6);
        var seven = await f.AddImageAsync(BlueUmf, cone: 7);
        var eight = await f.AddImageAsync(BlueUmf, cone: 8);
        var reduction = await f.AddImageAsync(BlueUmf, cone: 6, atmosphere: "reduction");
        var noCone = await f.AddImageAsync(BlueUmf, cone: null);
        var cone01 = await f.AddImageAsync(BlueUmf, cone: -1);
        var cone02 = await f.AddImageAsync(BlueUmf, cone: -2);
        var service = f.Service();
        async Task<Guid[]> Found(decimal cone) => (await service.SearchAsync(UmfQuery(BlueUmf, firing: new SearchFiringInput(cone, cone, "oxidation", false)), f.AsUser, null, CancellationToken.None))
            .Items.Select(x => x.ImageId).OrderBy(x => x).ToArray();

        // Meeting 2026-10-29: only ±1 cone, the same atmosphere, and never a tile whose cone wasn't recorded.
        Assert.True((await SearchSettings.GetAsync(f.Db, CancellationToken.None)).StrictFiring);
        Assert.Equal(new[] { six.Id, seven.Id }.OrderBy(x => x), await Found(6));
        // There is no cone 0, so cone 01 is next to cone 1, and 02 is two away.
        Assert.Equal([cone01.Id], await Found(1));
        Assert.Equal(new[] { cone01.Id, cone02.Id }.OrderBy(x => x), await Found(-1));

        await f.StrictFiringAsync(false);
        var all = await Found(6);
        Assert.Contains(eight.Id, all); Assert.Contains(reduction.Id, all); Assert.Contains(noCone.Id, all);
        // Turned off, a search can still ask for strict itself.
        var asked = await service.SearchAsync(UmfQuery(BlueUmf, firing: new SearchFiringInput(6, 6, "oxidation", true)), f.AsUser, null, CancellationToken.None);
        Assert.Equal(new[] { six.Id, seven.Id }.OrderBy(x => x), asked.Items.Select(x => x.ImageId).OrderBy(x => x));
    }

    [Fact]
    public void Cone_steps_skip_the_missing_cone_zero()
    {
        Assert.Equal(1m, HybridRanker.ConeSteps(-1, 1));
        Assert.Equal(3m, HybridRanker.ConeSteps(-2, 2));
        Assert.Equal(1m, HybridRanker.ConeSteps(5, 6));
        Assert.Equal((-1m, 2m), HybridRanker.ConeWindow(1, 1, 1));
        Assert.Equal((-10m, -8m), HybridRanker.ConeWindow(-9, -9, 1));
        Assert.Equal((-2m, 1m), HybridRanker.ConeWindow(-1, -1, 1));
    }

    [Fact]
    public async Task Nothing_above_threshold_returns_empty_items_with_no_reliable_match()
    {
        await using var f = new Fixture(); await f.InitAsync();
        await f.AddImageAsync(BlueUmf);
        f.Retrieval.MinimumConfidence = 0.999;
        var response = await f.Service().SearchAsync(UmfQuery(MatteUmf), f.AsUser, null, CancellationToken.None);
        Assert.Empty(response.Items);
        Assert.Contains("no_reliable_match", response.Warnings);
        Assert.Equal(0, (await f.Db.SearchRuns.SingleAsync()).ResultCount);
    }

    [Fact]
    public async Task Tile_without_chemistry_ranks_on_firing_and_text_with_reduced_confidence()
    {
        await using var f = new Fixture(); await f.InitAsync();
        var textOnly = await f.AddImageAsync(null);
        var response = await f.Service().SearchAsync(UmfQuery(BlueUmf), f.AsUser, null, CancellationToken.None);
        var item = Assert.Single(response.Items);
        Assert.Equal(textOnly.Id, item.ImageId);
        Assert.Null(item.ScoreBreakdown.ChemistrySimilarity);
        Assert.NotNull(item.ScoreBreakdown.TextSimilarity);
        Assert.NotEqual("high", item.ConfidenceBand);
        Assert.Contains(item.Limitations, l => l.StartsWith("No chemistry is recorded"));
    }

    [Fact]
    public async Task Idempotency_key_replays_the_same_run_and_rejects_a_different_query()
    {
        await using var f = new Fixture(); await f.InitAsync();
        await f.AddImageAsync(BlueUmf);
        var service = f.Service();
        var a = await service.SearchAsync(UmfQuery(BlueUmf), f.AsUser, "key-1", CancellationToken.None);
        var b = await service.SearchAsync(UmfQuery(BlueUmf), f.AsUser, "key-1", CancellationToken.None);
        Assert.Equal(a.SearchId, b.SearchId);
        Assert.Equal(1, await f.Db.SearchRuns.CountAsync());
        var ex = await Assert.ThrowsAsync<ApiProblemException>(() => service.SearchAsync(UmfQuery(MatteUmf), f.AsUser, "key-1", CancellationToken.None));
        Assert.Equal(("idempotency_key_reused", 422), (ex.Code, ex.Status));
    }

    [Fact]
    public async Task Stored_run_is_owner_only_and_re_authorised_on_read()
    {
        await using var f = new Fixture(); await f.InitAsync();
        var image = await f.AddImageAsync(BlueUmf);
        var service = f.Service();
        var run = await service.SearchAsync(UmfQuery(BlueUmf), f.AsUser, null, CancellationToken.None);
        Assert.Null(await service.GetAsync(run.SearchId, new SearchRequester(Guid.NewGuid(), false), CancellationToken.None));
        Assert.NotNull(await service.GetAsync(run.SearchId, new SearchRequester(f.Admin.Id, true), CancellationToken.None));

        image.ApprovalStatus = ApprovalStatus.Rejected; await f.Db.SaveChangesAsync();
        var again = await service.GetAsync(run.SearchId, f.AsUser, CancellationToken.None);
        Assert.Empty(again!.Items);
        Assert.Contains("results_changed_since_search", again.Warnings);
    }

    [Fact]
    public async Task Results_name_their_recipe_and_older_saved_runs_get_the_name_on_read()
    {
        await using var f = new Fixture(); await f.InitAsync();
        var image = await f.AddImageAsync(BlueUmf, recipeCode: "A03-B1-001");
        var recipe = await f.Db.Recipes.SingleAsync(x => x.Code == "A03-B1-001");
        recipe.Name = "Absence ∆10 · Lab 1 · Without Colorants";
        var baseGlaze = new BaseGlaze { Id = Guid.CreateVersion7(), OwnerUserId = f.User.Id, ParticipantCode = "A03", BaseNumber = 1, Code = "A03-B1", Name = "Absence ∆10 base" };
        f.Db.BaseGlazes.Add(baseGlaze); recipe.BaseGlazeId = baseGlaze.Id; await f.Db.SaveChangesAsync();
        var service = f.Service();
        var run = await service.SearchAsync(UmfQuery(BlueUmf) with { IncludeRecipeMetadata = true }, f.AsUser, null, CancellationToken.None);
        Assert.Equal("Absence ∆10 · Lab 1 · Without Colorants", run.Items[0].Source.RecipeName);
        Assert.Equal("A03-B1", run.Items[0].Source.BaseCode);                  // opens the recipe section on the base
        Assert.Equal("A03-B1", run.ClosestMatch!.Source.BaseCode);
        var hidden = await service.SearchAsync(UmfQuery(BlueUmf), f.AsUser, null, CancellationToken.None);
        Assert.Null(hidden.Items[0].Source.BaseCode);                          // image_only keeps the recipe out

        // A run saved before results carried the name: read back, it takes the recipe's current name.
        var stored = await f.Db.SearchRuns.SingleAsync(x => x.Id == run.SearchId);
        var items = System.Text.Json.Nodes.JsonNode.Parse(stored.ResultsJson)!.AsArray();
        foreach (var item in items) item!["source"]!.AsObject().Remove("recipeName");
        stored.ResultsJson = items.ToJsonString();
        recipe.Name = "Absence ∆10 · Lab 1 · Renamed";
        await f.Db.SaveChangesAsync();
        var again = await service.GetAsync(run.SearchId, f.AsUser, CancellationToken.None);
        Assert.Equal("Absence ∆10 · Lab 1 · Renamed", again!.Items[0].Source.RecipeName);
        Assert.Equal("A03-B1", again.Items[0].Source.BaseCode);
    }

    [Fact]
    public async Task Feedback_is_stored_once_per_result_and_only_for_returned_images()
    {
        await using var f = new Fixture(); await f.InitAsync();
        var image = await f.AddImageAsync(BlueUmf);
        var service = f.Service();
        var run = await service.SearchAsync(UmfQuery(BlueUmf), f.AsUser, null, CancellationToken.None);

        await service.RecordFeedbackAsync(new SearchFeedbackRequest(run.SearchId, image.Id, "relevant", "Close surface and cone match"), f.AsUser, CancellationToken.None);
        var updated = await service.RecordFeedbackAsync(new SearchFeedbackRequest(run.SearchId, image.Id, "not_relevant", null), f.AsUser, CancellationToken.None);
        Assert.Equal("not_relevant", updated.Feedback);
        Assert.Equal(1, await f.Db.SearchFeedback.CountAsync());

        Assert.Equal("image_not_in_results", (await Assert.ThrowsAsync<ApiProblemException>(() => service.RecordFeedbackAsync(new SearchFeedbackRequest(run.SearchId, Guid.NewGuid(), "relevant", null), f.AsUser, CancellationToken.None))).Code);
        Assert.Equal("search_not_found", (await Assert.ThrowsAsync<ApiProblemException>(() => service.RecordFeedbackAsync(new SearchFeedbackRequest(run.SearchId, image.Id, "relevant", null), new SearchRequester(f.Admin.Id, true), CancellationToken.None))).Code);
        Assert.Equal("validation_error", (await Assert.ThrowsAsync<ApiProblemException>(() => service.RecordFeedbackAsync(new SearchFeedbackRequest(run.SearchId, image.Id, "great", null), f.AsUser, CancellationToken.None))).Code);
    }

    [Fact]
    public async Task Recipe_queries_use_the_pinned_snapshot_and_refuse_recipes_without_chemistry()
    {
        await using var f = new Fixture(); await f.InitAsync();
        var image = await f.AddImageAsync(BlueUmf, recipeCode: "A03-B1-001");
        var service = f.Service();
        var byCode = await service.SearchAsync(new RecipeImageSearchRequest(new SearchQueryInput("recipe", null, null, null, null, null, "a03-b1-001", null, null), new SearchFiringInput(6, 6, "oxidation", false), null, 12, true), f.AsUser, null, CancellationToken.None);
        Assert.Equal(image.Id, byCode.Items[0].ImageId);
        Assert.Equal("A03-B1-001", byCode.Query.RecipeCode);
        Assert.Equal("recipe", byCode.Items[0].Source.MetadataVisibility);                                 // imported corpus recipe is visible
        Assert.Equal("A03-B1-001", byCode.Items[0].Source.RecipeCode);
        Assert.Equal("r", byCode.Items[0].Source.RecipeName);                                                // the fixture's recipe name

        var bare = new Recipe { Id = Guid.CreateVersion7(), Name = "coded, no UMF yet", OwnerUserId = f.User.Id, Code = "A03-B1-002" };
        f.Db.Recipes.Add(bare); await f.Db.SaveChangesAsync();
        var ex = await Assert.ThrowsAsync<ApiProblemException>(() => service.SearchAsync(new RecipeImageSearchRequest(new SearchQueryInput("recipe", null, null, null, null, bare.Id, null, null, null), null, null, 12, false), f.AsUser, null, CancellationToken.None));
        Assert.Equal(("recipe_chemistry_unavailable", 422), (ex.Code, ex.Status));

        // Recipes are system scoped: another participant's recipe is searchable by anyone signed in, and one that has
        // materials but no stored snapshot is calculated for the search (nothing is stored).
        var someoneElses = new Recipe { Id = Guid.CreateVersion7(), Name = "another participant's", OwnerUserId = Guid.NewGuid(), Code = "A04-B1-001",
            RecipeJson = """{"full":[{"rawName":"Custer Feldspar","amount":40,"unit":"%"},{"rawName":"Silica","amount":30,"unit":"%"},{"rawName":"Vapenec Whiting","amount":20,"unit":"%"},{"rawName":"Kaolin","amount":10,"unit":"%"}]}""" };
        f.Db.Recipes.Add(someoneElses); await f.Db.SaveChangesAsync();
        var theirs = await service.SearchAsync(new RecipeImageSearchRequest(new SearchQueryInput("recipe", null, null, null, null, null, "A04-B1-001", null, null), null, null, 12, false), f.AsUser, null, CancellationToken.None);
        Assert.Equal("A04-B1-001", theirs.Query.RecipeCode);
        Assert.True(theirs.Query.Umf!["SiO2"] > 0);
        Assert.Contains("calculated_for_this_search", theirs.Warnings);
        Assert.Empty(f.Db.RecipeCalculationResults.Where(x => x.RecipeId == someoneElses.Id));
    }

    [Fact]
    public async Task Invalid_input_is_rejected_with_field_errors_and_raw_recipes_report_unresolved_rows()
    {
        await using var f = new Fixture(); await f.InitAsync();
        var service = f.Service();
        var bad = await Assert.ThrowsAsync<ApiProblemException>(() => service.SearchAsync(
            new RecipeImageSearchRequest(new SearchQueryInput("umf", JsonDocument.Parse("""{"SiO2":3,"Unobtainium":1}""").RootElement, null, null, null, null, null, null, null), new SearchFiringInput(8, 6, "plasma", true), new SearchFiltersInput(["purple"], null, null, null), 500, false),
            f.AsUser, null, CancellationToken.None));
        Assert.Equal(("validation_error", 400), (bad.Code, bad.Status));
        var errors = (Dictionary<string, string[]>)bad.Extensions["errors"]!;
        Assert.Contains("query.umf", errors.Keys); Assert.Contains("topK", errors.Keys); Assert.Contains("firing.cone", errors.Keys);
        Assert.Contains("firing.atmosphere", errors.Keys); Assert.Contains("filters.tileVariantKeys", errors.Keys);

        var raw = await Assert.ThrowsAsync<ApiProblemException>(() => service.SearchAsync(
            new RecipeImageSearchRequest(new SearchQueryInput("raw_recipe", null, null, null, null, null, null, "Test blue glaze", [new("Potash Feldspar", 40, "%"), new("Silica", 30, "%")]), null, null, 12, false),
            f.AsUser, null, CancellationToken.None));
        Assert.Equal(("materials_unresolved", 422), (raw.Code, raw.Status));
        var resolutions = (IReadOnlyList<RowResolution>)raw.Extensions["resolutions"]!;
        Assert.Equal(["unresolved", "resolved"], resolutions.Select(r => r.Status));   // "Potash Feldspar" is not in the list; nothing is dropped silently
        Assert.Equal(0, await f.Db.SearchRuns.CountAsync());
    }

    [Fact]
    public async Task Raw_recipe_is_calculated_with_the_ported_calculator_and_searched()
    {
        await using var f = new Fixture(); await f.InitAsync();
        // Index a tile whose chemistry is the Leach 4321 UMF as the calculator produces it.
        var leach = GlazeChemistry.CalculateRecipe(TestChemistry.Leach, TestChemistry.Data())!.Chem.Umf;
        var image = await f.AddImageAsync(JsonSerializer.Serialize(leach));
        var response = await f.Service().SearchAsync(new RecipeImageSearchRequest(
            new SearchQueryInput("raw_recipe", null, null, null, null, null, null, "Leach 4321", [new("Custer Feldspar", 40, "%"), new("Silica", 30, "%"), new("Vapenec Whiting", 20, "%"), new("Kaolin", 10, "%")]),
            new SearchFiringInput(6, 6, "oxidation", false), null, 12, false), f.AsUser, null, CancellationToken.None);
        var top = response.Items[0];
        Assert.Equal(image.Id, top.ImageId);
        Assert.Equal(1.0, top.ScoreBreakdown.ChemistrySimilarity!.Value, 6);
        Assert.Equal("glazion-umf-v1", response.Query.CalculationProfile);
        // The apps send their UMF with calculationProfile "flux-to-1": same calculator, so the same profile.
        var fromApp = await f.Service().SearchAsync(new RecipeImageSearchRequest(
            new SearchQueryInput("umf", JsonSerializer.SerializeToElement(leach), null, "flux-to-1", null, null, null, null, null), null, null, 12, false), f.AsUser, null, CancellationToken.None);
        Assert.Equal("glazion-umf-v1", fromApp.Query.CalculationProfile);
        Assert.DoesNotContain(fromApp.Items[0].Limitations, l => l.Contains("across calculation profiles"));
        var run = await f.Db.SearchRuns.OrderBy(x => x.CreatedAt).FirstAsync();
        Assert.Equal("raw_recipe", run.QueryType);
        Assert.Contains("glazion-chemistry-1.0.0", run.QuerySnapshotJson);
    }

    [Fact]
    public async Task Raw_recipe_rows_can_name_materials_by_dropdown_id()
    {
        await using var f = new Fixture(); await f.InitAsync();
        var data = TestChemistry.Data();
        string Id(string name) => data.Materials.First(m => m.Name == name).Id;
        var leach = GlazeChemistry.CalculateRecipe(TestChemistry.Leach, data)!.Chem.Umf;
        var image = await f.AddImageAsync(JsonSerializer.Serialize(leach));
        RecipeImageSearchRequest Search(IReadOnlyList<RawMaterialInput> rows) =>
            new(new SearchQueryInput("raw_recipe", null, null, null, null, null, null, "Leach 4321", rows), new SearchFiringInput(6, 6, "oxidation", false), null, 12, false);

        var byName = await f.Service().SearchAsync(Search([new("Custer Feldspar", 40, "%"), new("Silica", 30, "%"), new("Vapenec Whiting", 20, "%"), new("Kaolin", 10, "%")]), f.AsUser, null, CancellationToken.None);
        // The id wins over the name: the misspelt name on the third row is ignored.
        var byId = await f.Service().SearchAsync(Search([new(null, 40, "%", Id("Custer Feldspar")), new(null, 30, "%", Id("Silica")), new("Wrong Name", 20, "%", Id("Vapenec Whiting")), new(null, 10, "%", Id("Kaolin"))]), f.AsUser, null, CancellationToken.None);
        Assert.Equal(byName.Query.InputHash, byId.Query.InputHash);   // same UMF, so the same search
        Assert.Equal(image.Id, byId.Items[0].ImageId);
        Assert.Equal(1.0, byId.Items[0].ScoreBreakdown.ChemistrySimilarity!.Value, 6);

        var unknown = await Assert.ThrowsAsync<ApiProblemException>(() => f.Service().SearchAsync(Search([new(null, 40, "%", "no-such-material"), new(null, 30, "%", Id("Silica"))]), f.AsUser, null, CancellationToken.None));
        Assert.Equal(("materials_unresolved", 422), (unknown.Code, unknown.Status));
        var resolutions = (IReadOnlyList<RowResolution>)unknown.Extensions["resolutions"]!;
        Assert.Equal([("unresolved", "no-such-material"), ("resolved", Id("Silica"))], resolutions.Select(r => (r.Status, r.RawName)));
    }
}

public sealed class SearchVisibilityTests
{
    private static SearchResultItem Item() => new(1, "real_image", Guid.NewGuid(), null, null, 0.91, "high",
        new SearchScoreBreakdown(0.95, 1, 0.6, null, 0.97, 0.04, 1, 0.5, 0.7, 0.9),
        ["silica", "alumina", "fluxBalance", "cone", "atmosphere", "colourants"],
        new SearchTileSummary("white", "glossy", null, "blue"), new SearchFiringSummary(6, 6, "oxidation"),
        new SearchSourceSummary("recipe", null, "A01-B1-001", "full"),
        ["Tile chemistry is a supplied UMF, not calculated from verified materials.", "Firing atmosphere was not recorded for this tile.",
         "Tile chemistry does not report Li2O from the query; unmeasured is not treated as zero."]);

    private static RecipeImageSearchResponse Response(SearchResultItem item) => new(Guid.NewGuid(),
        new SearchQuerySummary("raw_recipe", "sha256:x", "p", null, new Dictionary<string, decimal> { ["SiO2"] = 3.2m }),
        new SearchRankerSummary("v", 10, 1, 0.5), [item], [], DateTimeOffset.UtcNow, item, []);

    [Fact]
    public void A_regular_user_sees_the_match_but_not_the_chemistry_behind_it()
    {
        var response = Response(Item());

        var user = SearchVisibility.ForViewer(response, isAdmin: false);
        var admin = SearchVisibility.ForViewer(response, isAdmin: true);

        var tile = Assert.Single(user.Items);
        Assert.Equal((0.91, "high", "A01-B1-001"), (tile.Score, tile.ConfidenceBand, tile.Source.RecipeCode)); // the match itself stays
        Assert.Null(user.Query.Umf);
        Assert.All(new double?[] { tile.ScoreBreakdown.ChemistrySimilarity, tile.ScoreBreakdown.ChemistryCosine, tile.ScoreBreakdown.FiringCompatibility }, Assert.Null);
        Assert.Equal(["cone", "atmosphere", "colourants"], tile.MatchedFields);
        Assert.Equal(["Firing atmosphere was not recorded for this tile."], tile.Limitations);
        Assert.Null(user.ClosestMatch!.ScoreBreakdown.ChemistrySimilarity);
        Assert.Same(response, admin); // admins see it all
    }
}

public sealed class UmfSearchAccessTests
{
    private static Glazion.Api.Controllers.RecipeImageController Controller(bool admin)
    {
        var claims = new List<System.Security.Claims.Claim> { new(System.Security.Claims.ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()) };
        if (admin) claims.Add(new(System.Security.Claims.ClaimTypes.Role, "Admin"));
        return new Glazion.Api.Controllers.RecipeImageController(null!, Options.Create(new FeatureOptions { RecipeImageRetrieval = true }))
        {
            ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext
            {
                HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext { User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(claims, "test")) }
            }
        };
    }

    [Fact]
    public async Task Only_admins_search_by_umf()
    {
        var request = new RecipeImageSearchRequest(new SearchQueryInput("umf", JsonDocument.Parse("""{"SiO2":3}""").RootElement, null, null, null, null, null, null, null), null, null, 3, false);

        var refused = (await Controller(admin: false).Search(request, null, CancellationToken.None)).Result as Microsoft.AspNetCore.Mvc.ObjectResult;

        Assert.Equal(403, refused!.StatusCode);
        Assert.Contains("umf_search_not_available", System.Text.Json.JsonSerializer.Serialize(refused.Value));
    }
}
