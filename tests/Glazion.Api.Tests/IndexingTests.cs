using System.Net;
using System.Text;
using System.Text.Json;
using Glazion.Api.Domain;
using Glazion.Api.Indexing;
using Glazion.Api.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Glazion.Api.Tests;

public sealed class ChemistryVectorBuilderTests
{
    private static readonly Dictionary<string, decimal> Umf = new() { ["Na2O"] = 0.18m, ["K2O"] = 0.12m, ["CaO"] = 0.52m, ["MgO"] = 0.18m, ["Al2O3"] = 0.35m, ["B2O3"] = 0.20m, ["SiO2"] = 3.20m };

    [Fact]
    public void Values_follow_fixed_oxide_order_and_unmeasured_oxides_stay_distinguishable()
    {
        var vector = ChemistryVectorBuilder.Build(Umf);
        Assert.Equal(ChemistrySchema.OxideOrder.Count, vector.Dimension);
        Assert.Equal(0.18f, vector.Values[ChemistrySchema.OxideOrder.ToList().IndexOf("Na2O")]);
        Assert.Equal(3.20f, vector.Values[ChemistrySchema.OxideOrder.ToList().IndexOf("SiO2")]);
        var li = ChemistrySchema.OxideOrder.ToList().IndexOf("Li2O");
        Assert.Equal(0f, vector.Values[li]);
        Assert.False(vector.Measured[li]);
        Assert.Equal(7, vector.MeasuredCount);
        Assert.Equal(7.0 / 22, vector.Completeness, 6);
    }

    [Fact]
    public void Hash_is_stable_across_key_case_and_order_and_changes_with_values()
    {
        var a = ChemistryVectorBuilder.Build(Umf);
        var b = ChemistryVectorBuilder.Build(new Dictionary<string, decimal> { ["sio2"] = 3.2m, ["al2o3"] = 0.35m, ["b2o3"] = 0.2m, ["mgo"] = 0.18m, ["cao"] = 0.52m, ["k2o"] = 0.12m, ["na2o"] = 0.18m });
        var c = ChemistryVectorBuilder.Build(new Dictionary<string, decimal>(Umf) { ["SiO2"] = 3.21m });
        Assert.Equal(a.Hash, b.Hash);
        Assert.NotEqual(a.Hash, c.Hash);
        Assert.Equal(64, a.Hash.Length);
    }

    [Fact]
    public void Normalized_vector_has_unit_length_and_zero_vector_stays_zero()
    {
        var unit = ChemistryVectorBuilder.Build(Umf).Normalized();
        Assert.Equal(1.0, Math.Sqrt(unit.Sum(v => (double)v * v)), 5);
        var zero = new ChemistryVector(ChemistrySchema.Version, new float[22], new bool[22], "x").Normalized();
        Assert.All(zero, v => Assert.Equal(0f, v));
    }

    [Fact]
    public void Cosine_similarity_compares_direction_not_magnitude()
    {
        var a = ChemistryVectorBuilder.Build(Umf);
        var scaled = ChemistryVectorBuilder.Build(Umf.ToDictionary(x => x.Key, x => x.Value * 3));
        Assert.Equal(1.0, ChemistryVectorBuilder.CosineSimilarity(a.Values, scaled.Values), 5);
        var different = ChemistryVectorBuilder.Build(new Dictionary<string, decimal> { ["Li2O"] = 1m, ["ZrO2"] = 0.3m });
        Assert.Equal(0.0, ChemistryVectorBuilder.CosineSimilarity(a.Values, different.Values), 5);
    }

    [Fact]
    public void Invalid_umf_json_cannot_become_a_vector()
        => Assert.Throws<InvalidOperationException>(() => ChemistryVectorBuilder.FromUmfJson("""{"SiO2":3.2,"Unobtainium":1}"""));
}

public sealed class CanonicalTextBuilderTests
{
    [Fact]
    public void Produces_the_spec_document_shape_deterministically()
    {
        var input = new CanonicalTextInput("traditional-umf-v1",
            new Dictionary<string, decimal> { ["Na2O"] = 0.18m, ["K2O"] = 0.12m, ["CaO"] = 0.52m, ["MgO"] = 0.18m, ["Al2O3"] = 0.35m, ["B2O3"] = 0.20m, ["SiO2"] = 3.20m },
            6, 6, "oxidation", "white",
            new Dictionary<string, string> { ["surface"] = "glossy", ["opacity"] = "opaque", ["colourFamily"] = "blue" });
        var expected = string.Join('\n',
            "record_type: glaze_tile",
            "umf_profile: traditional-umf-v1",
            "fluxes: Na2O=0.1800; K2O=0.1200; MgO=0.1800; CaO=0.5200",
            "stabilizers: Al2O3=0.3500",
            "b2o3: 0.2000",
            "glass_formers: SiO2=3.2000",
            "silica_alumina_ratio: 9.1429",
            "cone: 6",
            "atmosphere: oxidation",
            "tile_variant: white",
            "surface: glossy",
            "opacity: opaque",
            "colour_family: blue") + "\n";
        Assert.Equal(expected, CanonicalTextBuilder.Build(input));
        Assert.Equal(CanonicalTextBuilder.Build(input), CanonicalTextBuilder.Build(input));
    }

    [Fact]
    public void Missing_chemistry_cone_range_and_absent_labels_are_handled_without_placeholders()
    {
        var text = CanonicalTextBuilder.Build(new CanonicalTextInput(null, null, 5, 6, null, "Off_White", new Dictionary<string, string> { ["texture"] = "Smooth" }));
        Assert.Equal("record_type: glaze_tile\nchemistry: unavailable\ncone: 5-6\ntile_variant: off_white\ntexture: smooth\n", text);
    }

    [Fact]
    public void Hash_is_versioned_and_stable()
    {
        var text = "record_type: glaze_tile\nchemistry: unavailable\ntile_variant: white\n";
        Assert.Equal(CanonicalTextBuilder.Hash(text), CanonicalTextBuilder.Hash(text));
        Assert.NotEqual(CanonicalTextBuilder.Hash(text), CanonicalTextBuilder.Hash(text + "surface: matte\n"));
    }
}

public sealed class ImageIndexDocumentBuilderTests
{
    private static ImageAsset Asset(ChemistryStatus chemistry, ApprovalStatus approval, QualityStatus quality) => new()
    {
        Id = Guid.Parse("15f1e6ad-9a78-4b1b-9e10-90a78c0d9ef0"),
        SourceVersion = 3,
        TileVariantKey = "white",
        ConeMin = 6, ConeMax = 6, Atmosphere = "oxidation",
        LabelsJson = """{"surface":"glossy","opacity":"opaque","colourFamily":"blue"}""",
        ChemistryStatus = chemistry, ApprovalStatus = approval, QualityStatus = quality, QualityPolicyVersion = "quality-policy-v1",
        CalculationResult = chemistry == ChemistryStatus.Linked ? new RecipeCalculationResult
        {
            Id = Guid.NewGuid(), RecipeId = Guid.Parse("6f7bf0a0-7d41-49d8-a1c9-7e1e9b236d2b"), CalculationProfile = "provided-umf-v1", CalculationVersion = "import-1.0.0", MaterialAnalysisVersion = "n/a",
            UmfJson = """{"Na2O":0.18,"K2O":0.12,"CaO":0.52,"MgO":0.18,"Al2O3":0.35,"B2O3":0.2,"SiO2":3.2}"""
        } : null,
        Recipes = chemistry == ChemistryStatus.Linked ? [new RecipeImage { RecipeId = Guid.Parse("6f7bf0a0-7d41-49d8-a1c9-7e1e9b236d2b"), Verified = true }] : []
    };

    [Fact]
    public void Approved_linked_image_yields_vector_text_and_allowlisted_metadata()
    {
        var document = ImageIndexDocumentBuilder.Build(Asset(ChemistryStatus.Linked, ApprovalStatus.Approved, QualityStatus.Passed));
        Assert.True(document.Indexable);
        Assert.NotNull(document.Chemistry);
        Assert.Contains("umf_profile: provided-umf-v1", document.CanonicalText);
        Assert.Equal("15f1e6ad-9a78-4b1b-9e10-90a78c0d9ef0", document.Metadata[VectorMetadataKeys.ImageId]);
        Assert.Equal(3L, document.Metadata[VectorMetadataKeys.SourceVersion]);
        Assert.Equal("approved", document.Metadata[VectorMetadataKeys.ApprovalStatus]);
        Assert.Equal(true, document.Metadata[VectorMetadataKeys.Indexable]);
        Assert.Equal(6.0, document.Metadata[VectorMetadataKeys.ConeMin]);
        Assert.Equal("blue", document.Metadata[VectorMetadataKeys.ColourFamily]);
        Assert.Equal("6f7bf0a0-7d41-49d8-a1c9-7e1e9b236d2b", document.Metadata[VectorMetadataKeys.RecipeId]);
        Assert.Equal(("oxide-vector-v1", "canonical-text-v1", "provided-umf-v1"), (document.Versions.ChemistrySchemaVersion, document.Versions.CanonicalTextVersion, document.Versions.CalculationProfile));
        Assert.DoesNotContain(document.Metadata.Keys, k => k.Contains("owner", StringComparison.OrdinalIgnoreCase) || k.Contains("key", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Pending_or_failed_images_are_never_indexable_and_missing_chemistry_has_no_vector()
    {
        var pending = ImageIndexDocumentBuilder.Build(Asset(ChemistryStatus.Missing, ApprovalStatus.Pending, QualityStatus.Passed));
        Assert.False(pending.Indexable);
        Assert.Null(pending.Chemistry);
        Assert.Contains("chemistry: unavailable", pending.CanonicalText);
        var failed = ImageIndexDocumentBuilder.Build(Asset(ChemistryStatus.Linked, ApprovalStatus.Approved, QualityStatus.Failed));
        Assert.False(failed.Indexable);
    }
}

public sealed class VectorIdsAndFilterTests
{
    [Fact]
    public void Vector_ids_round_trip()
    {
        var id = VectorIds.Image(Guid.Parse("15f1e6ad-9a78-4b1b-9e10-90a78c0d9ef0"), "oxide-vector-v1");
        Assert.Equal("image:15f1e6ad-9a78-4b1b-9e10-90a78c0d9ef0:oxide-vector-v1", id);
        Assert.True(VectorIds.TryParse(id, out var type, out var guid, out var version));
        Assert.Equal(("image", "oxide-vector-v1"), (type, version));
        Assert.Equal(Guid.Parse("15f1e6ad-9a78-4b1b-9e10-90a78c0d9ef0"), guid);
        Assert.False(VectorIds.TryParse("garbage", out _, out _, out _));
        Assert.Equal("images-chemistry-oxide-vector-v1", VectorNamespaces.ImageChemistry("oxide-vector-v1"));
    }

    [Fact]
    public void Filter_semantics_match_between_neutral_model_and_pinecone_translation()
    {
        var filter = VectorFilter.All(VectorFilter.Eq("approvalStatus", "approved"), VectorFilter.Eq("indexable", true), VectorFilter.AnyOf("tileVariant", ["white", "off_white"]), VectorFilter.Gte("coneMax", 5), VectorFilter.Lte("coneMin", 6));
        var yes = new Dictionary<string, object> { ["approvalStatus"] = "approved", ["indexable"] = true, ["tileVariant"] = "white", ["coneMin"] = 6.0, ["coneMax"] = 6.0 };
        var no = new Dictionary<string, object>(yes) { ["indexable"] = false };
        Assert.True(filter.Matches(yes));
        Assert.False(filter.Matches(no));
        Assert.False(filter.Matches(new Dictionary<string, object>(yes) { ["tileVariant"] = "pink" }));

        var json = JsonSerializer.Serialize(PineconeVectorIndex.TranslateFilter(filter));
        Assert.Equal("""{"$and":[{"approvalStatus":{"$eq":"approved"}},{"indexable":{"$eq":true}},{"tileVariant":{"$in":["white","off_white"]}},{"coneMax":{"$gte":5}},{"coneMin":{"$lte":6}}]}""", json);
    }
}

public sealed class InMemoryVectorIndexTests
{
    [Fact]
    public async Task Upsert_overwrites_same_id_query_ranks_by_cosine_and_respects_filters()
    {
        var index = new InMemoryVectorIndex();
        var ns = VectorNamespaces.ImageChemistry("oxide-vector-v1");
        await index.UpsertAsync(ns, [new VectorRecord("a", [1, 0, 0], new Dictionary<string, object> { ["indexable"] = true }), new VectorRecord("b", [0.9f, 0.1f, 0], new Dictionary<string, object> { ["indexable"] = true }), new VectorRecord("c", [1, 0, 0], new Dictionary<string, object> { ["indexable"] = false })], CancellationToken.None);
        await index.UpsertAsync(ns, [new VectorRecord("a", [0, 1, 0], new Dictionary<string, object> { ["indexable"] = true })], CancellationToken.None);

        var matches = await index.QueryAsync(new VectorQuery(ns, [1, 0, 0], 10, VectorFilter.Eq("indexable", true)), CancellationToken.None);
        Assert.Equal(["b", "a"], matches.Select(m => m.Id).ToArray());
        Assert.True(matches[0].Score > matches[1].Score);

        var stats = await index.DescribeAsync(CancellationToken.None);
        Assert.Equal((3, 3L), (stats.Dimension, stats.NamespaceCounts[ns]));
        await index.DeleteAsync(ns, ["a"], CancellationToken.None);
        Assert.Empty(await index.FetchAsync(ns, ["a"], CancellationToken.None));
        await Assert.ThrowsAsync<VectorIndexException>(() => index.UpsertAsync(ns, [new VectorRecord("d", [1, 2])], CancellationToken.None));
    }
}

public sealed class PineconeVectorIndexTests
{
    private sealed class FakeHandler(Func<HttpRequestMessage, int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string Body)> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request, body));
            return respond(request, Requests.Count);
        }
    }

    private static PineconeVectorIndex Create(FakeHandler handler, int batchSize = 100) => new(new HttpClient(handler),
        Options.Create(new VectorIndexOptions { Provider = "Pinecone", Pinecone = new PineconeOptions { ApiKey = "test-key", IndexHost = "https://idx.example.pinecone.io", UpsertBatchSize = batchSize, MaxRetries = 2, RetryBaseMilliseconds = 1 } }),
        NullLogger<PineconeVectorIndex>.Instance);

    private static HttpResponseMessage Json(HttpStatusCode status, string json) => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task Upsert_batches_and_sends_credentials_and_namespace()
    {
        var handler = new FakeHandler((_, _) => Json(HttpStatusCode.OK, """{"upsertedCount":2}"""));
        var index = Create(handler, batchSize: 2);
        var records = Enumerable.Range(0, 5).Select(i => new VectorRecord($"image:{Guid.NewGuid():D}:v1", [i, 1], new Dictionary<string, object> { ["indexable"] = true })).ToList();
        await index.UpsertAsync("images-chemistry-oxide-vector-v1", records, CancellationToken.None);

        Assert.Equal(3, handler.Requests.Count);
        var first = handler.Requests[0];
        Assert.Equal("https://idx.example.pinecone.io/vectors/upsert", first.Request.RequestUri!.ToString());
        Assert.Equal("test-key", first.Request.Headers.GetValues("Api-Key").Single());
        Assert.Equal("2025-01", first.Request.Headers.GetValues("X-Pinecone-API-Version").Single());
        using var doc = JsonDocument.Parse(first.Body);
        Assert.Equal("images-chemistry-oxide-vector-v1", doc.RootElement.GetProperty("namespace").GetString());
        Assert.Equal(2, doc.RootElement.GetProperty("vectors").GetArrayLength());
        Assert.True(doc.RootElement.GetProperty("vectors")[0].GetProperty("metadata").GetProperty("indexable").GetBoolean());
    }

    [Fact]
    public async Task Query_translates_filter_and_maps_matches()
    {
        var handler = new FakeHandler((_, _) => Json(HttpStatusCode.OK, """{"matches":[{"id":"image:15f1e6ad-9a78-4b1b-9e10-90a78c0d9ef0:v1","score":0.91,"metadata":{"imageId":"15f1e6ad-9a78-4b1b-9e10-90a78c0d9ef0","coneMin":6,"indexable":true}}],"namespace":"ns"}"""));
        var matches = await Create(handler).QueryAsync(new VectorQuery("ns", [1, 0], 50, VectorFilter.All(VectorFilter.Eq("approvalStatus", "approved"), VectorFilter.Eq("indexable", true))), CancellationToken.None);
        using var doc = JsonDocument.Parse(handler.Requests.Single().Body);
        Assert.Equal(50, doc.RootElement.GetProperty("topK").GetInt32());
        Assert.False(doc.RootElement.GetProperty("includeValues").GetBoolean());
        Assert.Equal("approved", doc.RootElement.GetProperty("filter").GetProperty("$and")[0].GetProperty("approvalStatus").GetProperty("$eq").GetString());
        var match = Assert.Single(matches);
        Assert.Equal(0.91, match.Score, 5);
        Assert.Equal(6L, match.Metadata!["coneMin"]);
        Assert.Equal(true, match.Metadata["indexable"]);
    }

    [Fact]
    public async Task Rate_limit_is_retried_then_surfaces_as_transient_error()
    {
        var limited = new FakeHandler((_, _) => Json(HttpStatusCode.TooManyRequests, """{"message":"slow down"}"""));
        var ex = await Assert.ThrowsAsync<VectorIndexException>(() => Create(limited).UpsertAsync("ns", [new VectorRecord("a", [1])], CancellationToken.None));
        Assert.Equal(VectorIndexErrorCode.RateLimited, ex.Code);
        Assert.True(ex.IsTransient);
        Assert.Equal(3, limited.Requests.Count); // 1 attempt + MaxRetries

        var recovering = new FakeHandler((_, n) => n == 1 ? Json(HttpStatusCode.ServiceUnavailable, "{}") : Json(HttpStatusCode.OK, """{"upsertedCount":1}"""));
        await Create(recovering).UpsertAsync("ns", [new VectorRecord("a", [1])], CancellationToken.None);
        Assert.Equal(2, recovering.Requests.Count);
    }

    [Fact]
    public async Task Client_errors_are_not_retried_and_map_to_neutral_codes()
    {
        var handler = new FakeHandler((_, _) => Json(HttpStatusCode.Unauthorized, """{"message":"bad key"}"""));
        var ex = await Assert.ThrowsAsync<VectorIndexException>(() => Create(handler).DescribeAsync(CancellationToken.None));
        Assert.Equal(VectorIndexErrorCode.Unauthorized, ex.Code);
        Assert.False(ex.IsTransient);
        Assert.Single(handler.Requests);
        Assert.DoesNotContain("test-key", ex.Message);
    }

    [Fact]
    public async Task Describe_and_fetch_parse_provider_shapes()
    {
        var handler = new FakeHandler((req, _) => req.RequestUri!.AbsolutePath.EndsWith("describe_index_stats")
            ? Json(HttpStatusCode.OK, """{"namespaces":{"images-chemistry-oxide-vector-v1":{"vectorCount":12000}},"dimension":22,"totalVectorCount":12000}""")
            : Json(HttpStatusCode.OK, """{"vectors":{"image:x:v1":{"id":"image:x:v1","values":[0.5,0.5],"metadata":{"tileVariant":"white"}}},"namespace":"ns"}"""));
        var index = Create(handler);
        var stats = await index.DescribeAsync(CancellationToken.None);
        Assert.Equal((22, 12000L), (stats.Dimension, stats.NamespaceCounts["images-chemistry-oxide-vector-v1"]));
        var fetched = await index.FetchAsync("ns", ["image:x:v1"], CancellationToken.None);
        Assert.Contains("ids=image%3Ax%3Av1", handler.Requests[1].Request.RequestUri!.Query);
        Assert.Equal("white", fetched["image:x:v1"].Metadata!["tileVariant"]);
    }
}
