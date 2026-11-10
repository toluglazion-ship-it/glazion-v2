using System.Text.Json;
using Glazion.Api.Data;
using Glazion.Api.Domain;
using Glazion.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Glazion.Api.Indexing;

/// <summary>Enqueues index work next to the state change that caused it, in the caller's transaction.</summary>
public static class IndexJobs
{
    public static Task<ProcessingJob> EnqueueUpsertAsync(GlazionDbContext db, Guid imageId, CancellationToken cancellationToken)
        => JobQueue.EnqueueAsync(db, JobTypes.IndexImageUpsert, EntityTypes.Image, imageId, JobQueue.DedupeKeyFor(JobTypes.IndexImageUpsert, imageId), null, cancellationToken);

    public static Task<ProcessingJob> EnqueueRemoveAsync(GlazionDbContext db, Guid imageId, CancellationToken cancellationToken)
        => JobQueue.EnqueueAsync(db, JobTypes.IndexImageRemove, EntityTypes.Image, imageId, JobQueue.DedupeKeyFor(JobTypes.IndexImageRemove, imageId), null, cancellationToken);
}

/// <summary>
/// Makes the vector index match PostgreSQL for one image. Both index.image.upsert and index.image.remove run this
/// same reconciliation, so job ordering cannot leave a stale vector behind: the current database state decides.
///
/// Guarantees (plan §12.3): deterministic vector IDs so re-runs overwrite; IndexRecords written only after the
/// provider confirms; a failed provider call leaves the prior good record untouched; a new embedding version is
/// upserted first and the old version's vector deleted and marked superseded only after the new one is live.
/// </summary>
public sealed class ImageIndexer(GlazionDbContext db, IVectorIndex vectorIndex, ITextEmbeddingProvider embeddings, ILogger<ImageIndexer> logger, Chemistry.ChemistryDataProvider? chemistry = null)
{
    private const string ChemistryModel = "none";

    public async Task<JobResult> SyncAsync(Guid imageId, CancellationToken cancellationToken)
    {
        var asset = await db.ImageAssets.Include(x => x.CalculationResult).Include(x => x.Recipes).ThenInclude(x => x.Recipe)
            .SingleOrDefaultAsync(x => x.Id == imageId, cancellationToken);
        if (asset is null) return JobResult.Fail("asset_not_found");

        IReadOnlyList<Chemistry.Colourant>? colourants = null;
        if (chemistry is not null && chemistry.TryGet(out var data, out _)) colourants = Chemistry.Colourants.ForAsset(asset, data, chemistry.Curation);
        var document = ImageIndexDocumentBuilder.Build(asset, colourants: colourants);
        var records = await db.IndexRecords.Where(x => x.EntityType == EntityTypes.Image && x.EntityId == imageId).ToListAsync(cancellationToken);

        try
        {
            if (!document.Indexable)
            {
                await RemoveAllAsync(records, cancellationToken);
                await db.SaveChangesAsync(cancellationToken);
                return JobResult.Succeeded;
            }

            var live = new List<IndexRecord>();
            if (document.Chemistry is { } chemistry)
                live.Add(await UpsertChemistryAsync(asset, document, chemistry, records, cancellationToken));
            else
                await RemoveModalityAsync(records, IndexModality.Chemistry, cancellationToken);

            live.Add(await UpsertTextAsync(asset, document, records, cancellationToken));

            // Supersede older versions of each live modality only now that the new vectors are confirmed.
            foreach (var current in live)
            {
                var stale = records.Where(r => r.Modality == current.Modality && r.Id != current.Id && r.SupersededAt is null).ToList();
                foreach (var old in stale)
                {
                    await vectorIndex.DeleteAsync(old.VectorNamespace, [old.VectorId], cancellationToken);
                    MarkSuperseded(old);
                }
            }
            await db.SaveChangesAsync(cancellationToken);
            logger.LogDebug("Image {ImageId} indexed at source version {SourceVersion} ({Modalities})", imageId, asset.SourceVersion, string.Join(",", live.Select(x => x.Modality)));
            await MarkUploadIndexedAsync(asset, cancellationToken);
            return JobResult.Succeeded;
        }
        catch (VectorIndexException ex)
        {
            // Record the failure on the existing manifest rows without changing their indexed state.
            foreach (var record in records) { record.LastErrorCode = ex.Code.ToString(); record.LastErrorDetail = Truncate(ex.Message); record.UpdatedAt = DateTimeOffset.UtcNow; }
            await db.SaveChangesAsync(cancellationToken);
            return ex.IsTransient ? JobResult.Retry(ex.Code.ToString(), ex.Message) : JobResult.Fail(ex.Code.ToString(), ex.Message);
        }
    }

    /// <summary>The participant's upload moves Approved → Indexed once its image is searchable (UploadStatus is part of the v2 contract).</summary>
    private async Task MarkUploadIndexedAsync(ImageAsset asset, CancellationToken cancellationToken)
    {
        if (asset.UploadId is not { } uploadId) return;
        var upload = await db.Uploads.SingleOrDefaultAsync(x => x.Id == uploadId && (x.Status == UploadStatus.Approved || x.Status == UploadStatus.Indexing), cancellationToken);
        if (upload is null) return;
        upload.Status = UploadStatus.Indexed;
        upload.ConcurrencyStamp = Guid.NewGuid();
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException)
        {
            // A reviewer changed the upload meanwhile (e.g. rejected it); that change queues its own index sync.
            db.Entry(upload).State = EntityState.Detached;
        }
    }

    private async Task<IndexRecord> UpsertChemistryAsync(ImageAsset asset, ImageIndexDocument document, ChemistryVector chemistry, List<IndexRecord> records, CancellationToken cancellationToken)
    {
        var ns = VectorNamespaces.ImageChemistry(chemistry.SchemaVersion);
        var vectorId = VectorIds.Image(asset.Id, chemistry.SchemaVersion);
        var metadata = WithModality(document.Metadata, "chemistry");
        await vectorIndex.UpsertAsync(ns, [new VectorRecord(vectorId, chemistry.Normalized(), metadata)], cancellationToken);
        var record = GetOrCreate(records, asset.Id, ns, vectorId, IndexModality.Chemistry, ChemistryModel, chemistry.SchemaVersion);
        Confirm(record, asset, document, metadata, chemistryHash: chemistry.Hash);
        return record;
    }

    private async Task<IndexRecord> UpsertTextAsync(ImageAsset asset, ImageIndexDocument document, List<IndexRecord> records, CancellationToken cancellationToken)
    {
        var ns = VectorNamespaces.ImageText(embeddings.Version);
        var vectorId = VectorIds.Image(asset.Id, embeddings.Version);
        var metadata = WithModality(document.Metadata, "text");
        var existing = records.FirstOrDefault(r => r.VectorNamespace == ns && r.VectorId == vectorId && r.IndexedAt is not null);

        // Same canonical text under the same embedding version: reuse the stored vector and only refresh metadata,
        // so a status or approval change never spends an embedding call.
        float[]? values = null;
        if (existing is not null && existing.CanonicalTextHash == document.CanonicalTextHash)
        {
            var fetched = await vectorIndex.FetchAsync(ns, [vectorId], cancellationToken);
            if (fetched.TryGetValue(vectorId, out var stored) && stored.Values.Length == embeddings.Dimension) values = stored.Values;
        }
        values ??= (await embeddings.EmbedAsync([document.CanonicalText], cancellationToken))[0];

        await vectorIndex.UpsertAsync(ns, [new VectorRecord(vectorId, values, metadata)], cancellationToken);
        var record = GetOrCreate(records, asset.Id, ns, vectorId, IndexModality.Text, embeddings.Model, embeddings.Version);
        Confirm(record, asset, document, metadata, chemistryHash: document.Chemistry?.Hash);
        return record;
    }

    private async Task RemoveAllAsync(List<IndexRecord> records, CancellationToken cancellationToken)
    {
        foreach (var record in records.Where(r => r.SupersededAt is null))
        {
            await vectorIndex.DeleteAsync(record.VectorNamespace, [record.VectorId], cancellationToken);
            MarkSuperseded(record);
        }
    }

    private async Task RemoveModalityAsync(List<IndexRecord> records, IndexModality modality, CancellationToken cancellationToken)
    {
        foreach (var record in records.Where(r => r.Modality == modality && r.SupersededAt is null))
        {
            await vectorIndex.DeleteAsync(record.VectorNamespace, [record.VectorId], cancellationToken);
            MarkSuperseded(record);
        }
    }

    private IndexRecord GetOrCreate(List<IndexRecord> records, Guid imageId, string ns, string vectorId, IndexModality modality, string model, string version)
    {
        var record = records.FirstOrDefault(r => r.VectorNamespace == ns && r.VectorId == vectorId);
        if (record is not null) return record;
        record = new IndexRecord { Id = Guid.CreateVersion7(), EntityType = EntityTypes.Image, EntityId = imageId, Modality = modality, EmbeddingModel = model, EmbeddingVersion = version, VectorNamespace = ns, VectorId = vectorId };
        db.IndexRecords.Add(record);
        records.Add(record);
        return record;
    }

    private void Confirm(IndexRecord record, ImageAsset asset, ImageIndexDocument document, IReadOnlyDictionary<string, object> metadata, string? chemistryHash)
    {
        var now = DateTimeOffset.UtcNow;
        record.VectorProvider = vectorIndex.ProviderName;
        record.SourceVersion = asset.SourceVersion;
        record.Indexable = true;
        record.ApprovalStatus = asset.ApprovalStatus;
        record.ChemistrySchemaVersion = document.Versions.ChemistrySchemaVersion;
        record.ChemistryVectorHash = chemistryHash;
        record.CanonicalTextHash = document.CanonicalTextHash;
        record.ImageSha256 = asset.Sha256;
        record.MetadataJson = JsonSerializer.Serialize(metadata);
        record.IndexedAt = now;
        record.SupersededAt = null;
        record.LastErrorCode = null;
        record.LastErrorDetail = null;
        record.UpdatedAt = now;
    }

    private static void MarkSuperseded(IndexRecord record)
    {
        record.Indexable = false;
        record.SupersededAt = DateTimeOffset.UtcNow;
        record.UpdatedAt = record.SupersededAt.Value;
    }

    private static Dictionary<string, object> WithModality(IReadOnlyDictionary<string, object> metadata, string modality)
        => new(metadata, StringComparer.Ordinal) { [VectorMetadataKeys.Modality] = modality };

    private static string Truncate(string value) => value.Length > 2000 ? value[..2000] : value;
}

public sealed class IndexImageUpsertHandler(IVectorIndex vectorIndex, ITextEmbeddingProvider embeddings, ILoggerFactory loggerFactory, Chemistry.ChemistryDataProvider chemistry) : IJobHandler
{
    public string JobType => JobTypes.IndexImageUpsert;
    public Task<JobResult> HandleAsync(ProcessingJob job, GlazionDbContext db, CancellationToken cancellationToken)
        => new ImageIndexer(db, vectorIndex, embeddings, loggerFactory.CreateLogger<ImageIndexer>(), chemistry).SyncAsync(job.EntityId, cancellationToken);
}

public sealed class IndexImageRemoveHandler(IVectorIndex vectorIndex, ITextEmbeddingProvider embeddings, ILoggerFactory loggerFactory, Chemistry.ChemistryDataProvider chemistry) : IJobHandler
{
    public string JobType => JobTypes.IndexImageRemove;
    public Task<JobResult> HandleAsync(ProcessingJob job, GlazionDbContext db, CancellationToken cancellationToken)
        => new ImageIndexer(db, vectorIndex, embeddings, loggerFactory.CreateLogger<ImageIndexer>(), chemistry).SyncAsync(job.EntityId, cancellationToken);
}
