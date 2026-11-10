using Glazion.Api.Contracts;
using Glazion.Api.Data;
using Glazion.Api.Domain;
using Glazion.Api.Indexing;
using Glazion.Api.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.EntityFrameworkCore;

namespace Glazion.Api.Controllers;

public sealed record IndexNamespaceStatus(string Namespace, IndexModality Modality, string EmbeddingModel, string EmbeddingVersion, int Indexed, int Superseded, int Errored, long? ProviderVectorCount);
public sealed record IndexJobCounts(int Queued, int Running, int Succeeded, int Failed, int DeadLetter);
public sealed record IndexStatusResponse(int ApprovedImages, int IndexedImages, int Backlog, string VectorProvider, string EmbeddingModel, string EmbeddingVersion, string ChemistrySchemaVersion, IReadOnlyCollection<IndexNamespaceStatus> Namespaces, IndexJobCounts Jobs, string? ProviderError);
public sealed record RebuildIndexRequest(IReadOnlyCollection<Guid>? ImageIds, bool IncludeNonApproved);
public sealed record RebuildIndexResponse(int Enqueued);
public sealed record JobResponse(Guid Id, string JobType, string EntityType, Guid EntityId, JobStatus Status, int Attempts, int MaxAttempts, string? ErrorCode, string? ErrorDetail, DateTimeOffset AvailableAt, DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt, DateTimeOffset CreatedAt);

/// <summary>Index operations for the admin dashboard: backlog, per-namespace manifest, rebuilds, and job inspection (plan §15).</summary>
[ApiController]
[Route("api/v2/admin")]
[Authorize(Roles = "Admin")]
public sealed class AdminIndexController(GlazionDbContext db, IVectorIndex vectorIndex, ITextEmbeddingProvider embeddings, ILogger<AdminIndexController> logger) : ControllerBase
{
    [HttpGet("index/status")]
    public async Task<ActionResult<IndexStatusResponse>> Status(CancellationToken cancellationToken)
    {
        var approved = await db.ImageAssets.CountAsync(x => x.ApprovalStatus == ApprovalStatus.Approved && x.QualityStatus != QualityStatus.Failed, cancellationToken);
        // Only records this store confirmed count as indexed: right after a switch to Pinecone the backlog shows the re-send.
        var provider = vectorIndex.ProviderName;
        var legacyCounts = provider == "InMemory"; // records written before the provider was recorded came from the in-memory index
        var indexedImages = await db.IndexRecords.Where(x => x.EntityType == EntityTypes.Image && x.Indexable && x.IndexedAt != null && x.SupersededAt == null
                && (x.VectorProvider == provider || (legacyCounts && x.VectorProvider == null)))
            .Select(x => x.EntityId).Distinct().CountAsync(cancellationToken);
        var namespaces = await db.IndexRecords.Where(x => x.EntityType == EntityTypes.Image)
            .GroupBy(x => new { x.VectorNamespace, x.Modality, x.EmbeddingModel, x.EmbeddingVersion })
            .Select(g => new { g.Key, Indexed = g.Count(x => x.Indexable && x.IndexedAt != null), Superseded = g.Count(x => x.SupersededAt != null), Errored = g.Count(x => x.LastErrorCode != null) })
            .ToListAsync(cancellationToken);
        var indexJobTypes = new[] { JobTypes.IndexImageUpsert, JobTypes.IndexImageRemove };
        var jobs = await db.ProcessingJobs.Where(x => indexJobTypes.Contains(x.JobType)).GroupBy(x => x.Status).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, cancellationToken);

        IReadOnlyDictionary<string, long>? providerCounts = null;
        string? providerError = null;
        try { providerCounts = (await vectorIndex.DescribeAsync(cancellationToken)).NamespaceCounts; }
        catch (Exception ex) when (ex is VectorIndexException or InvalidOperationException) { providerError = ex.Message; }

        var jobCounts = new IndexJobCounts(jobs.GetValueOrDefault(JobStatus.Queued), jobs.GetValueOrDefault(JobStatus.Running), jobs.GetValueOrDefault(JobStatus.Succeeded), jobs.GetValueOrDefault(JobStatus.Failed), jobs.GetValueOrDefault(JobStatus.DeadLetter));
        return Ok(new IndexStatusResponse(approved, indexedImages, Math.Max(0, approved - indexedImages), vectorIndex.ProviderName, embeddings.Model, embeddings.Version, ChemistrySchema.Version,
            namespaces.Select(n => new IndexNamespaceStatus(n.Key.VectorNamespace, n.Key.Modality, n.Key.EmbeddingModel, n.Key.EmbeddingVersion, n.Indexed, n.Superseded, n.Errored, providerCounts is not null && providerCounts.TryGetValue(n.Key.VectorNamespace, out var c) ? c : null)).ToList(),
            jobCounts, providerError));
    }

    /// <summary>Enqueues index sync for approved images (or the given IDs). Idempotent: an already queued image is not duplicated.</summary>
    [HttpPost("index/rebuild")]
    public async Task<ActionResult<RebuildIndexResponse>> Rebuild([FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] RebuildIndexRequest? request, CancellationToken cancellationToken)
    {
        var query = db.ImageAssets.AsNoTracking().AsQueryable();
        if (request?.ImageIds is { Count: > 0 } ids) query = query.Where(x => ids.Contains(x.Id));
        else if (request?.IncludeNonApproved != true) query = query.Where(x => x.ApprovalStatus == ApprovalStatus.Approved && x.QualityStatus != QualityStatus.Failed);
        var imageIds = await query.Select(x => x.Id).ToListAsync(cancellationToken);
        foreach (var imageId in imageIds) await IndexJobs.EnqueueUpsertAsync(db, imageId, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Index rebuild enqueued for {Count} images by {AdminId}", imageIds.Count, User.GetUserId());
        return Ok(new RebuildIndexResponse(imageIds.Count));
    }

    /// <summary>Newest first, up to 500 per page.</summary>
    [HttpGet("jobs")]
    public Task<PagedResult<JobResponse>> Jobs([FromQuery] JobStatus? status, [FromQuery] string? jobType, [FromQuery] string? cursor, [FromQuery] int? pageSize, CancellationToken cancellationToken)
    {
        var query = db.ProcessingJobs.AsNoTracking().AsQueryable();
        if (status is not null) query = query.Where(x => x.Status == status);
        if (!string.IsNullOrWhiteSpace(jobType)) query = query.Where(x => x.JobType == jobType);
        return Paging.PageAsync(query.OrderByDescending(x => x.CreatedAt).ThenBy(x => x.Id), cursor, pageSize,
            jobs => Task.FromResult<IReadOnlyList<JobResponse>>(jobs.Select(j => new JobResponse(j.Id, j.JobType, j.EntityType, j.EntityId, j.Status, j.Attempts, j.MaxAttempts, j.ErrorCode, j.ErrorDetail, j.AvailableAt, j.StartedAt, j.CompletedAt, j.CreatedAt)).ToList()),
            cancellationToken, maxPageSize: 500);
    }

    /// <summary>Re-queues a failed or dead-lettered job after the cause is fixed. Attempts reset; the handler is idempotent.</summary>
    [HttpPost("jobs/{jobId:guid}/retry")]
    public async Task<IActionResult> Retry(Guid jobId, CancellationToken cancellationToken)
    {
        var job = await db.ProcessingJobs.SingleOrDefaultAsync(x => x.Id == jobId, cancellationToken);
        if (job is null) return NotFound();
        if (job.Status is JobStatus.Queued or JobStatus.Running) return Conflict(new { error = $"Job is {job.Status}." });
        job.Status = JobStatus.Queued; job.Attempts = 0; job.AvailableAt = DateTimeOffset.UtcNow; job.CompletedAt = null; job.StartedAt = null; job.LeaseExpiresAt = null;
        await db.SaveChangesAsync(cancellationToken);
        return NoContent();
    }
}
