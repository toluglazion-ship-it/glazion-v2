using System.Text.Json;
using Glazion.Api.Data;
using Glazion.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Glazion.Api.Infrastructure;

public static class JobTypes
{
    public const string ImageValidate = "image.validate";
    public const string IndexImageUpsert = "index.image.upsert";
    public const string IndexImageRemove = "index.image.remove";
    /// <summary>Re-creates derivatives and metrics for an already validated image (backfill, policy change). Validation does this itself for new images.</summary>
    public const string ImageDerivativesCreate = "image.derivatives.create";
}

public static class EntityTypes
{
    public const string Image = "image";
}

public enum JobResultKind { Succeeded, Retry, Fail }
public sealed record JobResult(JobResultKind Kind, string? ErrorCode = null, string? ErrorDetail = null)
{
    public static readonly JobResult Succeeded = new(JobResultKind.Succeeded);
    public static JobResult Retry(string code, string? detail = null) => new(JobResultKind.Retry, code, detail);
    public static JobResult Fail(string code, string? detail = null) => new(JobResultKind.Fail, code, detail);
}

public interface IJobHandler
{
    string JobType { get; }
    Task<JobResult> HandleAsync(ProcessingJob job, GlazionDbContext db, CancellationToken cancellationToken);
}

/// <summary>
/// Enqueues into the caller's DbContext without saving, so a job is committed in the same transaction as the
/// state it processes (PRD §13: "PostgreSQL transactions for approval plus indexing-job creation").
/// </summary>
public static class JobQueue
{
    public static async Task<ProcessingJob> EnqueueAsync(GlazionDbContext db, string jobType, string entityType, Guid entityId, string dedupeKey, object? payload, CancellationToken cancellationToken)
    {
        var existing = db.ProcessingJobs.Local.FirstOrDefault(x => x.DedupeKey == dedupeKey)
                       ?? await db.ProcessingJobs.FirstOrDefaultAsync(x => x.DedupeKey == dedupeKey, cancellationToken);
        if (existing is null)
        {
            var job = new ProcessingJob
            {
                Id = Guid.CreateVersion7(),
                JobType = jobType,
                EntityType = entityType,
                EntityId = entityId,
                DedupeKey = dedupeKey,
                PayloadJson = payload is null ? "{}" : JsonSerializer.Serialize(payload)
            };
            db.ProcessingJobs.Add(job);
            return job;
        }
        if (existing.Status is JobStatus.Queued or JobStatus.Running) return existing;
        existing.Status = JobStatus.Queued;
        existing.Attempts = 0;
        existing.ErrorCode = null;
        existing.ErrorDetail = null;
        existing.AvailableAt = DateTimeOffset.UtcNow;
        existing.StartedAt = null;
        existing.LeaseExpiresAt = null;
        existing.CompletedAt = null;
        if (payload is not null) existing.PayloadJson = JsonSerializer.Serialize(payload);
        return existing;
    }

    public static string DedupeKeyFor(string jobType, Guid entityId) => $"{jobType}:{entityId:D}";
}

/// <summary>
/// PostgreSQL-backed worker. Claims use FOR UPDATE SKIP LOCKED so several API instances can run the worker
/// concurrently without double-processing; expired leases are reclaimed; failures back off exponentially and
/// dead-letter after MaxAttempts. Handlers must be idempotent because a lease can expire mid-run.
/// </summary>
public sealed class JobWorker(IServiceScopeFactory scopeFactory, IOptions<WorkerOptions> options, ILogger<JobWorker> logger) : BackgroundService
{
    private readonly WorkerOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Job worker started with concurrency {Concurrency}", _options.Concurrency);
        var slots = new SemaphoreSlim(Math.Max(1, _options.Concurrency));
        while (!stoppingToken.IsCancellationRequested)
        {
            await slots.WaitAsync(stoppingToken);
            Guid? jobId = null;
            try { jobId = await ClaimAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { slots.Release(); break; }
            catch (Exception ex) { logger.LogError(ex, "Job claim failed"); }

            if (jobId is null)
            {
                slots.Release();
                try { await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, _options.PollIntervalSeconds)), stoppingToken); }
                catch (OperationCanceledException) { break; }
                continue;
            }

            _ = Task.Run(async () =>
            {
                try { await RunAsync(jobId.Value, stoppingToken); }
                catch (Exception ex) { logger.LogError(ex, "Job {JobId} crashed outside handler", jobId); }
                finally { slots.Release(); }
            }, stoppingToken);
        }
    }

    private async Task<Guid?> ClaimAsync(CancellationToken cancellationToken)
    {
        // Executed as a plain command: EF's SqlQuery wraps SQL in a subquery, and PostgreSQL does not allow a
        // data-modifying statement (UPDATE ... RETURNING) inside one.
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GlazionDbContext>();
        var connection = db.Database.GetDbConnection();
        await db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE glazion."ProcessingJobs"
                SET "Status" = 'Running', "Attempts" = "Attempts" + 1, "StartedAt" = now(), "LeaseExpiresAt" = now() + make_interval(secs => @lease)
                WHERE "Id" = (
                    SELECT "Id" FROM glazion."ProcessingJobs"
                    WHERE ("Status" = 'Queued' AND "AvailableAt" <= now())
                       OR ("Status" = 'Running' AND "LeaseExpiresAt" < now())
                    ORDER BY "AvailableAt"
                    LIMIT 1
                    FOR UPDATE SKIP LOCKED)
                RETURNING "Id"
                """;
            var lease = command.CreateParameter();
            lease.ParameterName = "lease";
            lease.Value = (double)_options.LeaseSeconds;
            command.Parameters.Add(lease);
            var result = await command.ExecuteScalarAsync(cancellationToken);
            return result is Guid id ? id : null;
        }
        finally { await db.Database.CloseConnectionAsync(); }
    }

    private async Task RunAsync(Guid jobId, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GlazionDbContext>();
        var job = await db.ProcessingJobs.SingleAsync(x => x.Id == jobId, cancellationToken);
        var handler = scope.ServiceProvider.GetServices<IJobHandler>().FirstOrDefault(x => x.JobType == job.JobType);
        JobResult result;
        if (handler is null) result = JobResult.Fail("no_handler", $"No handler registered for job type '{job.JobType}'.");
        else
        {
            try { result = await handler.HandleAsync(job, db, cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Job {JobId} ({JobType}) attempt {Attempt} threw", job.Id, job.JobType, job.Attempts);
                result = JobResult.Retry("exception", ex.GetType().Name + ": " + ex.Message);
            }
        }

        var now = DateTimeOffset.UtcNow;
        switch (result.Kind)
        {
            case JobResultKind.Succeeded:
                job.Status = JobStatus.Succeeded; job.CompletedAt = now; job.ErrorCode = null; job.ErrorDetail = null; job.LeaseExpiresAt = null;
                break;
            case JobResultKind.Fail:
                job.Status = JobStatus.Failed; job.CompletedAt = now; job.ErrorCode = result.ErrorCode; job.ErrorDetail = Truncate(result.ErrorDetail); job.LeaseExpiresAt = null;
                break;
            case JobResultKind.Retry when job.Attempts >= job.MaxAttempts:
                job.Status = JobStatus.DeadLetter; job.CompletedAt = now; job.ErrorCode = result.ErrorCode; job.ErrorDetail = Truncate(result.ErrorDetail); job.LeaseExpiresAt = null;
                logger.LogError("Job {JobId} ({JobType}) dead-lettered after {Attempts} attempts: {Code}", job.Id, job.JobType, job.Attempts, result.ErrorCode);
                break;
            default:
                var delay = TimeSpan.FromSeconds(_options.RetryBaseSeconds * Math.Pow(2, job.Attempts - 1)) + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 1000));
                job.Status = JobStatus.Queued; job.AvailableAt = now + delay; job.ErrorCode = result.ErrorCode; job.ErrorDetail = Truncate(result.ErrorDetail); job.LeaseExpiresAt = null;
                break;
        }
        await db.SaveChangesAsync(cancellationToken);
        // One line per job floods the log during bulk reindexing (Railway drops lines past its rate limit), so a
        // success is Debug; anything else stays visible.
        logger.Log(job.Status == JobStatus.Succeeded ? LogLevel.Debug : LogLevel.Information,
            "Job {JobId} ({JobType}) for {EntityType} {EntityId} -> {Status}", job.Id, job.JobType, job.EntityType, job.EntityId, job.Status);
    }

    private static string? Truncate(string? value) => value is { Length: > 2000 } ? value[..2000] : value;
}
