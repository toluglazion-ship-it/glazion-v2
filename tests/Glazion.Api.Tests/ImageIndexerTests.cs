using Glazion.Api.Data;
using Glazion.Api.Domain;
using Glazion.Api.Indexing;
using Glazion.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Glazion.Api.Tests;

public sealed class HashingTextEmbeddingProviderTests
{
    private static HashingTextEmbeddingProvider Provider(int dimension = 256) => new(Options.Create(new EmbeddingOptions { Hashing = new HashingEmbeddingOptions { Dimension = dimension } }));

    [Fact]
    public async Task Is_deterministic_unit_length_and_labelled_as_non_semantic()
    {
        var provider = Provider();
        var text = "record_type: glaze_tile\ncone: 6\nsurface: glossy\n";
        var a = (await provider.EmbedAsync([text], CancellationToken.None))[0];
        var b = (await provider.EmbedAsync([text], CancellationToken.None))[0];
        Assert.Equal(a, b);
        Assert.Equal(256, a.Length);
        Assert.Equal(1.0, Math.Sqrt(a.Sum(v => (double)v * v)), 5);
        Assert.Equal("hashing-256:v1", provider.Version);
        Assert.Equal("feature-hashing", provider.Model);
    }

    [Fact]
    public async Task Shared_lines_score_higher_than_unrelated_text()
    {
        var provider = Provider();
        var vectors = await provider.EmbedAsync([
            "record_type: glaze_tile\ncone: 6\natmosphere: oxidation\nsurface: glossy\ncolour_family: blue\n",
            "record_type: glaze_tile\ncone: 6\natmosphere: oxidation\nsurface: glossy\ncolour_family: green\n",
            "record_type: glaze_tile\ncone: 10\natmosphere: reduction\nsurface: matte\ncolour_family: brown\n"], CancellationToken.None);
        Assert.True(ChemistryVectorBuilder.CosineSimilarity(vectors[0], vectors[1]) > ChemistryVectorBuilder.CosineSimilarity(vectors[0], vectors[2]));
    }
}

public sealed class ImageIndexerTests
{
    private static readonly Guid RecipeId = Guid.Parse("6f7bf0a0-7d41-49d8-a1c9-7e1e9b236d2b");

    private static GlazionDbContext NewDb() => new(new DbContextOptionsBuilder<GlazionDbContext>().UseInMemoryDatabase("indexer-" + Guid.NewGuid()).Options);
    private static HashingTextEmbeddingProvider Embeddings() => new(Options.Create(new EmbeddingOptions()));

    private static async Task<ImageAsset> SeedAsync(GlazionDbContext db, ApprovalStatus approval, bool withChemistry = true, QualityStatus quality = QualityStatus.Passed)
    {
        var recipe = new Recipe { Id = RecipeId, Name = "Cone 6 blue", SourceType = RecipeSourceType.Imported };
        var snapshot = new RecipeCalculationResult { Id = Guid.CreateVersion7(), RecipeId = RecipeId, CalculationProfile = "provided-umf-v1", CalculationVersion = "import-1.0.0", MaterialAnalysisVersion = "n/a", ChemistrySchemaVersion = ChemistrySchema.Version, InputHash = "h", UmfJson = """{"Na2O":0.18,"K2O":0.12,"CaO":0.52,"MgO":0.18,"Al2O3":0.35,"B2O3":0.2,"SiO2":3.2}""" };
        var asset = new ImageAsset
        {
            Id = Guid.CreateVersion7(), SourceType = ImageSourceType.Import, ImportRecordId = Guid.NewGuid(), OriginalKey = "prod/originals/default/imports/b/r/source.png", DeclaredContentType = "image/png", DeclaredSizeBytes = 1,
            TileVariantKey = "white", ConeMin = 6, ConeMax = 6, Atmosphere = "oxidation", LabelsJson = """{"surface":"glossy","colourFamily":"blue"}""", Sha256 = new string('a', 64),
            QualityStatus = quality, ApprovalStatus = approval, SourceVersion = 2,
            ChemistryStatus = withChemistry ? ChemistryStatus.Linked : ChemistryStatus.Missing, CalculationResultId = withChemistry ? snapshot.Id : null
        };
        db.Recipes.Add(recipe); db.RecipeCalculationResults.Add(snapshot); db.ImageAssets.Add(asset);
        if (withChemistry) db.RecipeImages.Add(new RecipeImage { RecipeId = RecipeId, ImageId = asset.Id, Verified = true });
        await db.SaveChangesAsync();
        return asset;
    }

    [Fact]
    public async Task Approved_image_is_upserted_into_both_namespaces_and_manifest_written_after_confirmation()
    {
        await using var db = NewDb();
        var index = new InMemoryVectorIndex();
        var embeddings = Embeddings();
        var asset = await SeedAsync(db, ApprovalStatus.Approved);

        var result = await new ImageIndexer(db, index, embeddings, NullLogger<ImageIndexer>.Instance).SyncAsync(asset.Id, CancellationToken.None);

        Assert.Equal(JobResultKind.Succeeded, result.Kind);
        var chemistryNs = VectorNamespaces.ImageChemistry(ChemistrySchema.Version);
        var textNs = VectorNamespaces.ImageText(embeddings.Version);
        var chem = await index.FetchAsync(chemistryNs, [VectorIds.Image(asset.Id, ChemistrySchema.Version)], CancellationToken.None);
        var text = await index.FetchAsync(textNs, [VectorIds.Image(asset.Id, embeddings.Version)], CancellationToken.None);
        Assert.Single(chem); Assert.Single(text);
        Assert.Equal(22, chem.Values.Single().Values.Length);
        Assert.Equal(256, text.Values.Single().Values.Length);
        Assert.Equal("chemistry", chem.Values.Single().Metadata![VectorMetadataKeys.Modality]);
        Assert.Equal(true, text.Values.Single().Metadata![VectorMetadataKeys.Indexable]);
        Assert.Equal(RecipeId.ToString("D"), text.Values.Single().Metadata![VectorMetadataKeys.RecipeId]);

        var records = await db.IndexRecords.Where(x => x.EntityId == asset.Id).ToListAsync();
        Assert.Equal(2, records.Count);
        Assert.All(records, r => { Assert.True(r.Indexable); Assert.NotNull(r.IndexedAt); Assert.Null(r.SupersededAt); Assert.Equal(2, r.SourceVersion); Assert.Equal(ApprovalStatus.Approved, r.ApprovalStatus); });
        Assert.Contains(records, r => r.Modality == IndexModality.Chemistry && r.EmbeddingVersion == ChemistrySchema.Version && r.ChemistryVectorHash is not null);
        Assert.Contains(records, r => r.Modality == IndexModality.Text && r.EmbeddingModel == "feature-hashing");
    }

    [Fact]
    public async Task Rerun_is_idempotent_and_reuses_the_text_vector_when_canonical_text_is_unchanged()
    {
        await using var db = NewDb();
        var index = new InMemoryVectorIndex();
        var counting = new CountingEmbeddings(Embeddings());
        var asset = await SeedAsync(db, ApprovalStatus.Approved);
        var indexer = new ImageIndexer(db, index, counting, NullLogger<ImageIndexer>.Instance);

        await indexer.SyncAsync(asset.Id, CancellationToken.None);
        asset.SourceVersion = 3; await db.SaveChangesAsync();      // metadata-only change, same canonical text
        await indexer.SyncAsync(asset.Id, CancellationToken.None);

        Assert.Equal(1, counting.Calls);
        Assert.Equal(2, await db.IndexRecords.CountAsync(x => x.EntityId == asset.Id));
        Assert.Equal(3, (await db.IndexRecords.FirstAsync(x => x.EntityId == asset.Id && x.Modality == IndexModality.Text)).SourceVersion);
        var stats = await index.DescribeAsync(CancellationToken.None);
        Assert.Equal(2, stats.TotalVectorCount);
    }

    [Fact]
    public async Task Rejected_or_failed_image_is_removed_and_records_marked_superseded()
    {
        await using var db = NewDb();
        var index = new InMemoryVectorIndex();
        var asset = await SeedAsync(db, ApprovalStatus.Approved);
        var indexer = new ImageIndexer(db, index, Embeddings(), NullLogger<ImageIndexer>.Instance);
        await indexer.SyncAsync(asset.Id, CancellationToken.None);
        Assert.Equal(2, (await index.DescribeAsync(CancellationToken.None)).TotalVectorCount);

        asset.ApprovalStatus = ApprovalStatus.Rejected; asset.SourceVersion++; await db.SaveChangesAsync();
        var result = await indexer.SyncAsync(asset.Id, CancellationToken.None);

        Assert.Equal(JobResultKind.Succeeded, result.Kind);
        Assert.Equal(0, (await index.DescribeAsync(CancellationToken.None)).TotalVectorCount);
        var records = await db.IndexRecords.Where(x => x.EntityId == asset.Id).ToListAsync();
        Assert.Equal(2, records.Count);
        Assert.All(records, r => { Assert.False(r.Indexable); Assert.NotNull(r.SupersededAt); });
    }

    [Fact]
    public async Task Missing_chemistry_indexes_text_only()
    {
        await using var db = NewDb();
        var index = new InMemoryVectorIndex();
        var asset = await SeedAsync(db, ApprovalStatus.Approved, withChemistry: false);
        await new ImageIndexer(db, index, Embeddings(), NullLogger<ImageIndexer>.Instance).SyncAsync(asset.Id, CancellationToken.None);
        var stats = await index.DescribeAsync(CancellationToken.None);
        Assert.Equal(1, stats.TotalVectorCount);
        Assert.Single(await db.IndexRecords.Where(x => x.EntityId == asset.Id).ToListAsync(), r => r.Modality == IndexModality.Text);
    }

    [Fact]
    public async Task New_embedding_version_supersedes_old_vector_only_after_new_one_is_live()
    {
        await using var db = NewDb();
        var index = new InMemoryVectorIndex();
        var asset = await SeedAsync(db, ApprovalStatus.Approved);
        var v1 = new HashingTextEmbeddingProvider(Options.Create(new EmbeddingOptions { Hashing = new HashingEmbeddingOptions { Dimension = 128 } }));
        var v2 = new HashingTextEmbeddingProvider(Options.Create(new EmbeddingOptions { Hashing = new HashingEmbeddingOptions { Dimension = 256 } }));
        await new ImageIndexer(db, index, v1, NullLogger<ImageIndexer>.Instance).SyncAsync(asset.Id, CancellationToken.None);
        await new ImageIndexer(db, index, v2, NullLogger<ImageIndexer>.Instance).SyncAsync(asset.Id, CancellationToken.None);

        var stats = await index.DescribeAsync(CancellationToken.None);
        Assert.Equal(0L, stats.NamespaceCounts.GetValueOrDefault(VectorNamespaces.ImageText(v1.Version)));
        Assert.Equal(1L, stats.NamespaceCounts[VectorNamespaces.ImageText(v2.Version)]);
        var textRecords = await db.IndexRecords.Where(x => x.EntityId == asset.Id && x.Modality == IndexModality.Text).ToListAsync();
        Assert.Equal(2, textRecords.Count);
        Assert.NotNull(textRecords.Single(r => r.EmbeddingVersion == v1.Version).SupersededAt);
        Assert.Null(textRecords.Single(r => r.EmbeddingVersion == v2.Version).SupersededAt);
    }

    [Fact]
    public async Task Transient_provider_failure_requests_retry_and_leaves_prior_good_record_intact()
    {
        await using var db = NewDb();
        var good = new InMemoryVectorIndex();
        var asset = await SeedAsync(db, ApprovalStatus.Approved);
        await new ImageIndexer(db, good, Embeddings(), NullLogger<ImageIndexer>.Instance).SyncAsync(asset.Id, CancellationToken.None);
        var before = await db.IndexRecords.AsNoTracking().Where(x => x.EntityId == asset.Id).Select(x => x.IndexedAt).ToListAsync();

        var failing = new ThrowingVectorIndex(new VectorIndexException(VectorIndexErrorCode.RateLimited, "429"));
        var result = await new ImageIndexer(db, failing, Embeddings(), NullLogger<ImageIndexer>.Instance).SyncAsync(asset.Id, CancellationToken.None);

        Assert.Equal(JobResultKind.Retry, result.Kind);
        var after = await db.IndexRecords.AsNoTracking().Where(x => x.EntityId == asset.Id).ToListAsync();
        Assert.Equal(before, after.Select(x => x.IndexedAt).ToList());
        Assert.All(after, r => { Assert.True(r.Indexable); Assert.Equal("RateLimited", r.LastErrorCode); });

        var invalid = new ThrowingVectorIndex(new VectorIndexException(VectorIndexErrorCode.InvalidRequest, "bad"));
        Assert.Equal(JobResultKind.Fail, (await new ImageIndexer(db, invalid, Embeddings(), NullLogger<ImageIndexer>.Instance).SyncAsync(asset.Id, CancellationToken.None)).Kind);
    }

    private sealed class CountingEmbeddings(ITextEmbeddingProvider inner) : ITextEmbeddingProvider
    {
        public int Calls { get; private set; }
        public string Model => inner.Model; public string Version => inner.Version; public int Dimension => inner.Dimension;
        public Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct) { Calls++; return inner.EmbedAsync(texts, ct); }
    }

    private sealed class ThrowingVectorIndex(VectorIndexException error) : IVectorIndex
    {
        public string ProviderName => "Throwing";
        public Task UpsertAsync(string ns, IReadOnlyList<VectorRecord> records, CancellationToken ct) => throw error;
        public Task DeleteAsync(string ns, IReadOnlyList<string> ids, CancellationToken ct) => throw error;
        public Task<IReadOnlyList<VectorMatch>> QueryAsync(VectorQuery query, CancellationToken ct) => throw error;
        public Task<IReadOnlyDictionary<string, VectorRecord>> FetchAsync(string ns, IReadOnlyList<string> ids, CancellationToken ct) => throw error;
        public Task<VectorIndexStats> DescribeAsync(CancellationToken ct) => throw error;
    }
}
