using Glazion.Api.Contracts;
using Glazion.Api.Data;
using Glazion.Api.Domain;
using Glazion.Api.Indexing;
using Microsoft.EntityFrameworkCore;

namespace Glazion.Api.Infrastructure;

/// <summary>
/// Records the clay body on images that were captured before it was collected. Each changed image gets a new
/// SourceVersion and, when approved, an index sync, which refreshes the vector metadata without a new embedding.
/// </summary>
public static class ClayBodyBackfill
{
    private const int ChunkSize = 500;

    /// <summary>Which images a backfill touches: the listed ids, an import batch, or a participant's images.</summary>
    public sealed record Selector(IReadOnlyList<Guid>? ImageIds = null, Guid? ImportBatchId = null, string? ParticipantCode = null);

    public static Task<SetClayBodyResponse> ApplyAsync(GlazionDbContext db, string clayBody, IReadOnlyList<Guid>? imageIds, Guid? importBatchId, bool onlyIfMissing, CancellationToken cancellationToken) =>
        ApplyAsync(db, clayBody, new Selector(imageIds, importBatchId), onlyIfMissing, dryRun: false, cancellationToken);

    /// <summary>With <paramref name="dryRun"/> nothing is written; the response says what would change.</summary>
    public static async Task<SetClayBodyResponse> ApplyAsync(GlazionDbContext db, string clayBody, Selector selector, bool onlyIfMissing, bool dryRun, CancellationToken cancellationToken)
    {
        var query = Select(db, selector);
        var ids = await query.OrderBy(x => x.Id).Select(x => x.Id).ToListAsync(cancellationToken);
        if (dryRun)
        {
            var changing = query.Where(x => x.ClayBody != clayBody && (!onlyIfMissing || x.ClayBody == null));
            return new SetClayBodyResponse(clayBody, ids.Count, await changing.CountAsync(cancellationToken),
                await changing.CountAsync(x => x.ApprovalStatus == ApprovalStatus.Approved, cancellationToken), DryRun: true);
        }

        int updated = 0, reindexed = 0;
        foreach (var chunk in ids.Chunk(ChunkSize))
        {
            var assets = await db.ImageAssets.Where(x => chunk.Contains(x.Id)).ToListAsync(cancellationToken);
            foreach (var asset in assets)
            {
                if (asset.ClayBody == clayBody || (onlyIfMissing && asset.ClayBody is not null)) continue;
                asset.ClayBody = clayBody;
                asset.SourceVersion++;
                asset.UpdatedAt = DateTimeOffset.UtcNow;
                asset.ConcurrencyStamp = Guid.NewGuid();
                updated++;
                if (asset.ApprovalStatus == ApprovalStatus.Approved)
                {
                    await IndexJobs.EnqueueUpsertAsync(db, asset.Id, cancellationToken);
                    reindexed++;
                }
            }
            await db.SaveChangesAsync(cancellationToken);
            db.ChangeTracker.Clear();
        }
        return new SetClayBodyResponse(clayBody, ids.Count, updated, reindexed);
    }

    /// <summary>
    /// A participant's images are the ones they uploaded plus the ones linked to their coded recipes
    /// (P01-B3-007), which covers tiles that came in through an import batch on their behalf.
    /// </summary>
    public static IQueryable<ImageAsset> Select(GlazionDbContext db, Selector selector)
    {
        var query = db.ImageAssets.AsQueryable();
        var selectors = (selector.ImageIds is { Count: > 0 } ? 1 : 0) + (selector.ImportBatchId is not null ? 1 : 0) + (string.IsNullOrWhiteSpace(selector.ParticipantCode) ? 0 : 1);
        if (selectors != 1) throw ApiProblems.Validation("imageIds", "Give exactly one of imageIds, importBatchId or participantCode.");
        if (selector.ImageIds is { Count: > 0 } ids) return query.Where(x => ids.Contains(x.Id));
        if (selector.ImportBatchId is { } batchId) return query.Where(x => x.ImportRecord != null && x.ImportRecord.BatchId == batchId);
        return ForParticipant(query, selector.ParticipantCode!);
    }

    /// <summary>A participant's images: the ones they uploaded plus the ones linked to their coded recipes.</summary>
    public static IQueryable<ImageAsset> ForParticipant(IQueryable<ImageAsset> query, string participantCode)
    {
        var code = LabCode.NormalizeParticipant(participantCode);
        if (!LabCode.IsValidParticipant(code)) throw ApiProblems.Validation("participantCode", "Participant code must be a letter followed by two digits, e.g. P01.");
        var prefix = code + "-";
        return query.Where(x => (x.Upload != null && x.Upload.OwnerUser.ParticipantCode == code)
                                || x.Recipes.Any(r => r.Recipe.Code != null && r.Recipe.Code.StartsWith(prefix)));
    }

    /// <summary>Where the images came from, with how many already carry a clay body: one row per import batch and per participant.</summary>
    public static async Task<IReadOnlyList<ImageSourceGroup>> SourcesAsync(GlazionDbContext db, CancellationToken cancellationToken)
    {
        var groups = new List<ImageSourceGroup>();
        var batches = await db.ImportBatches.AsNoTracking().OrderBy(x => x.CreatedAt).Select(x => new { x.Id, x.Name, x.SourceLabel, x.CreatedAt }).ToListAsync(cancellationToken);
        foreach (var b in batches)
        {
            var images = db.ImageAssets.Where(x => x.ImportRecord != null && x.ImportRecord.BatchId == b.Id);
            groups.Add(new ImageSourceGroup("importBatch", b.Id.ToString("D"), $"{b.Name} ({b.SourceLabel}, {b.CreatedAt:yyyy-MM-dd})",
                await images.CountAsync(cancellationToken), await images.CountAsync(x => x.ClayBody != null, cancellationToken)));
        }
        var participants = await db.Users.AsNoTracking().Where(x => x.ParticipantCode != null).OrderBy(x => x.ParticipantCode).Select(x => new { x.ParticipantCode, x.Username }).ToListAsync(cancellationToken);
        foreach (var p in participants)
        {
            var images = Select(db, new Selector(ParticipantCode: p.ParticipantCode));
            var count = await images.CountAsync(cancellationToken);
            if (count > 0) groups.Add(new ImageSourceGroup("participant", p.ParticipantCode!, $"{p.ParticipantCode} ({p.Username})", count, await images.CountAsync(x => x.ClayBody != null, cancellationToken)));
        }
        var total = db.ImageAssets.AsQueryable();
        groups.Add(new ImageSourceGroup("all", "all", "All images", await total.CountAsync(cancellationToken), await total.CountAsync(x => x.ClayBody != null, cancellationToken)));
        return groups;
    }
}
