using Glazion.Api.Contracts;
using Glazion.Api.Data;
using Glazion.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Glazion.Api.Infrastructure;

/// <summary>What a reset clears (or, in a dry run, would clear), table by table, and what it keeps.</summary>
public sealed record LabResetReport(bool DryRun, string? BackupSchema, IReadOnlyDictionary<string, int> Cleared, IReadOnlyList<string> BasesCleared,
    IReadOnlyList<string> BasesKept, int ImportedRecipesKept, int ImportedImagesKept, int UsersKept, int IndexedUploadImages, IReadOnlyList<string> Notes);

/// <summary>A backup made by a reset: its schema and the rows in each table.</summary>
public sealed record LabBackupInfo(string Schema, IReadOnlyDictionary<string, long> Rows);

/// <summary>
/// Starts the lab work afresh (the client's "clear all", meeting 2026-11-09), keeping the imported dataset and every
/// user. Cleared: cup experiments, tile tasks, participants' uploads and their images, and the bases and recipes that
/// are not part of the import (with their codes, calculations and links). A base is kept with all its variations when
/// any of them is imported or carries an imported photo. Before deleting, every row it removes is copied into a backup
/// schema in the same database (lab_backup_…), which stays until it is dropped on purpose. Run without confirmation it
/// only reports.
/// </summary>
public sealed class LabDataReset(GlazionDbContext db, ILogger<LabDataReset> logger)
{
    public const string ConfirmPhrase = "CLEAR LAB DATA";
    public const string DropBackupPhrase = "DELETE BACKUP";
    private const string BackupPrefix = "lab_backup_";

    private sealed record Plan(List<Guid> Experiments, List<Guid> Tasks, List<Guid> Uploads, List<Guid> UploadImages, List<Guid> Recipes, List<Guid> Bases,
        List<string> BasesCleared, List<string> BasesKept, int IndexedUploadImages);

    private async Task<Plan> PlanAsync(CancellationToken ct)
    {
        var experiments = await db.Experiments.Select(x => x.Id).ToListAsync(ct);
        var tasks = await db.TileTasks.Select(x => x.Id).ToListAsync(ct);
        var uploads = await db.Uploads.Select(x => x.Id).ToListAsync(ct);
        var uploadImages = await db.ImageAssets.Where(x => x.SourceType == ImageSourceType.UserUpload).Select(x => x.Id).ToListAsync(ct);
        var indexed = await db.IndexRecords.CountAsync(x => x.EntityType == "image" && x.Indexable && uploadImages.Contains(x.EntityId), ct);

        // A recipe belongs to the import when it was imported or one of its photos was.
        var importRecipeIds = (await db.Recipes.Where(r => r.SourceType == RecipeSourceType.Imported
                || db.RecipeImages.Any(ri => ri.RecipeId == r.Id && ri.Image.SourceType == ImageSourceType.Import)
                || db.ImportRecords.Any(ir => ir.RecipeId == r.Id))
            .Select(r => r.Id).ToListAsync(ct)).ToHashSet();
        var bases = await db.BaseGlazes.Select(b => new { b.Id, b.Code, b.DerivedFromBaseId, b.PromotedFromRecipeId }).ToListAsync(ct);
        var recipeBase = await db.Recipes.Where(r => r.BaseGlazeId != null).Select(r => new { r.Id, BaseId = r.BaseGlazeId!.Value }).ToListAsync(ct);
        var keptBases = recipeBase.Where(r => importRecipeIds.Contains(r.Id)).Select(r => r.BaseId).ToHashSet();
        // A kept base keeps what it was made from.
        bool grew;
        do
        {
            grew = false;
            foreach (var b in bases.Where(b => keptBases.Contains(b.Id)).ToList())
            {
                if (b.DerivedFromBaseId is { } parent && keptBases.Add(parent)) grew = true;
                if (b.PromotedFromRecipeId is { } recipe && recipeBase.FirstOrDefault(r => r.Id == recipe) is { } rb && keptBases.Add(rb.BaseId)) grew = true;
            }
        } while (grew);
        var keptRecipes = importRecipeIds.Concat(recipeBase.Where(r => keptBases.Contains(r.BaseId)).Select(r => r.Id))
            .Concat(bases.Where(b => keptBases.Contains(b.Id) && b.PromotedFromRecipeId != null).Select(b => b.PromotedFromRecipeId!.Value)).ToHashSet();
        var recipes = (await db.Recipes.Select(r => r.Id).ToListAsync(ct)).Where(id => !keptRecipes.Contains(id)).ToList();
        var clearedBases = bases.Where(b => !keptBases.Contains(b.Id)).ToList();
        return new Plan(experiments, tasks, uploads, uploadImages, recipes, clearedBases.Select(b => b.Id).ToList(),
            clearedBases.Select(b => b.Code).OrderBy(c => c).ToList(), bases.Where(b => keptBases.Contains(b.Id)).Select(b => b.Code).OrderBy(c => c).ToList(), indexed);
    }

    /// <summary>Each table cleared, the column its rows are picked by and which of the plan's lists picks them (in delete order).</summary>
    private static IEnumerable<(string Table, string Column, Func<Plan, List<Guid>> Ids)> Steps() =>
    [
        ("ExperimentSteps", "ExperimentId", p => p.Experiments),
        ("Experiments", "Id", p => p.Experiments),
        ("TileTaskEvents", "TileTaskId", p => p.Tasks),
        ("TileTasks", "Id", p => p.Tasks),
        ("ProcessingJobs", "EntityId", p => [.. p.Uploads, .. p.UploadImages]),
        ("SearchFeedback", "ImageId", p => p.UploadImages),
        ("IndexRecords", "EntityId", p => p.UploadImages),
        ("RecipeImages", "ImageId", p => p.UploadImages),
        ("AiImageCopies", "ImageId", p => p.UploadImages),
        ("ImageAssets", "Id", p => p.UploadImages),
        ("Uploads", "Id", p => p.Uploads),
        ("CodeRecipes", "RecipeId", p => p.Recipes),
        ("RecipeImages", "RecipeId", p => p.Recipes),
        ("RecipeCalculationResults", "RecipeId", p => p.Recipes),
        ("Recipes", "Id", p => p.Recipes),
        ("BaseGlazes", "Id", p => p.Bases),
    ];

    public async Task<LabResetReport> RunAsync(string? confirm, Guid adminId, CancellationToken ct)
    {
        var dryRun = confirm != ConfirmPhrase;
        var plan = await PlanAsync(ct);
        var notes = new List<string>();
        if (plan.IndexedUploadImages > 0)
            notes.Add($"{plan.IndexedUploadImages} participant photos are in the search index; clearing them leaves their search entries behind until the index is rebuilt.");
        var counts = new Dictionary<string, int>();

        if (dryRun)
        {
            foreach (var (table, column, ids) in Steps())
                counts[$"{table} by {column}"] = await CountAsync(table, column, ids(plan), ct);
        }
        else
        {
            if (!db.Database.IsRelational()) throw new InvalidOperationException("A reset needs the real database.");
            var schema = $"{BackupPrefix}{DateTimeOffset.UtcNow:yyyyMMdd_HHmm}";
            var app = db.Model.GetDefaultSchema() ?? "public"; // the app's tables live in "glazion"
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
#pragma warning disable EF1002 // table and column names come from the fixed list above, never from a request
            await db.Database.ExecuteSqlRawAsync($"CREATE SCHEMA \"{schema}\"", ct);
            // 1. Copy every row about to go, as it is now (one backup table per source table).
            var backedUp = new HashSet<string>();
            foreach (var (table, column, ids) in Steps())
            {
                var list = ids(plan).Distinct().ToArray();
                if (backedUp.Add(table))
                    await db.Database.ExecuteSqlRawAsync($"CREATE TABLE \"{schema}\".\"{table}\" AS SELECT * FROM \"{app}\".\"{table}\" WHERE \"{column}\" = ANY({{0}})", [list], ct);
                else
                    await db.Database.ExecuteSqlRawAsync($"INSERT INTO \"{schema}\".\"{table}\" SELECT * FROM \"{app}\".\"{table}\" WHERE \"{column}\" = ANY({{0}}) " +
                        $"EXCEPT SELECT * FROM \"{schema}\".\"{table}\"", [list], ct);
            }
            // 2. Let go of links between rows that all go, so the deletes don't trip over each other.
            await db.Database.ExecuteSqlRawAsync($"UPDATE \"{app}\".\"ImageAssets\" SET \"DisplayCopyId\" = NULL WHERE \"Id\" = ANY({{0}})", [plan.UploadImages.ToArray()], ct);
            await db.Database.ExecuteSqlRawAsync($"UPDATE \"{app}\".\"BaseGlazes\" SET \"DerivedFromBaseId\" = NULL, \"PromotedFromRecipeId\" = NULL WHERE \"Id\" = ANY({{0}})", [plan.Bases.ToArray()], ct);
            // 3. Delete, dependents first.
            foreach (var (table, column, ids) in Steps())
                counts[$"{table} by {column}"] = await db.Database.ExecuteSqlRawAsync($"DELETE FROM \"{app}\".\"{table}\" WHERE \"{column}\" = ANY({{0}})", [ids(plan).Distinct().ToArray()], ct);
#pragma warning restore EF1002
            await transaction.CommitAsync(ct);
            logger.LogWarning("Admin {AdminId} cleared the lab data into backup {Schema}: {Counts}", adminId, schema, string.Join(", ", counts.Select(c => $"{c.Key} {c.Value}")));
            return await ReportAsync(false, schema, counts, plan, notes, ct);
        }
        return await ReportAsync(true, null, counts, plan, notes, ct);
    }

    private async Task<LabResetReport> ReportAsync(bool dryRun, string? schema, Dictionary<string, int> counts, Plan plan, List<string> notes, CancellationToken ct) =>
        new(dryRun, schema, counts, plan.BasesCleared, plan.BasesKept,
            await db.Recipes.CountAsync(r => r.SourceType == RecipeSourceType.Imported, ct),
            await db.ImageAssets.CountAsync(x => x.SourceType == ImageSourceType.Import, ct), await db.Users.CountAsync(ct), plan.IndexedUploadImages, notes);

    /// <summary>The rows a step would remove, counted the same way the delete picks them.</summary>
    private async Task<int> CountAsync(string table, string column, List<Guid> ids, CancellationToken ct)
    {
        var set = ids.ToHashSet();
        return (table, column) switch
        {
            ("ExperimentSteps", _) => await db.ExperimentSteps.CountAsync(x => set.Contains(x.ExperimentId), ct),
            ("Experiments", _) => set.Count,
            ("TileTaskEvents", _) => await db.TileTaskEvents.CountAsync(x => set.Contains(x.TileTaskId), ct),
            ("TileTasks", _) => set.Count,
            ("ProcessingJobs", _) => await db.ProcessingJobs.CountAsync(x => set.Contains(x.EntityId), ct),
            ("SearchFeedback", _) => await db.SearchFeedback.CountAsync(x => set.Contains(x.ImageId), ct),
            ("IndexRecords", _) => await db.IndexRecords.CountAsync(x => set.Contains(x.EntityId), ct),
            ("RecipeImages", "ImageId") => await db.RecipeImages.CountAsync(x => set.Contains(x.ImageId), ct),
            ("RecipeImages", _) => await db.RecipeImages.CountAsync(x => set.Contains(x.RecipeId), ct),
            ("AiImageCopies", _) => await db.AiImageCopies.CountAsync(x => set.Contains(x.ImageId), ct),
            ("CodeRecipes", _) => await db.CodeRecipes.CountAsync(x => set.Contains(x.RecipeId), ct),
            ("RecipeCalculationResults", _) => await db.RecipeCalculationResults.CountAsync(x => set.Contains(x.RecipeId), ct),
            _ => set.Count,
        };
    }

    /// <summary>The backups made by resets, with the rows kept in each table.</summary>
    public async Task<IReadOnlyList<LabBackupInfo>> BackupsAsync(CancellationToken ct)
    {
        if (!db.Database.IsRelational()) return [];
        var rows = await db.Database.SqlQueryRaw<BackupTableRow>(
            "SELECT n.nspname AS \"Schema\", c.relname AS \"Table\", c.reltuples::bigint AS \"Rows\" FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace " +
            $"WHERE c.relkind = 'r' AND n.nspname LIKE '{BackupPrefix}%' ORDER BY 1, 2").ToListAsync(ct);
        var result = new List<LabBackupInfo>();
        foreach (var g in rows.GroupBy(r => r.Schema))
        {
            var exact = new Dictionary<string, long>();
#pragma warning disable EF1002 // names read from pg_class just above
            foreach (var r in g) exact[r.Table] = await db.Database.SqlQueryRaw<long>($"SELECT COUNT(*) AS \"Value\" FROM \"{g.Key}\".\"{r.Table}\"").SingleAsync(ct);
#pragma warning restore EF1002
            result.Add(new LabBackupInfo(g.Key, exact));
        }
        return result;
    }

    private sealed record BackupTableRow(string Schema, string Table, long Rows);

    /// <summary>Drops a backup made by a reset, once the client has approved the fresh start. Only lab_backup_ schemas.</summary>
    public async Task DropBackupAsync(string schema, string? confirm, Guid adminId, CancellationToken ct)
    {
        if (confirm != DropBackupPhrase) throw ApiProblems.Validation("confirm", $"Send confirm \"{DropBackupPhrase}\" to delete a backup.");
        if (!System.Text.RegularExpressions.Regex.IsMatch(schema, $"^{BackupPrefix}[0-9_]+$")) throw ApiProblems.Validation("schema", "That is not a reset backup.");
        if ((await BackupsAsync(ct)).All(b => b.Schema != schema)) throw ApiProblems.NotFound("Backup");
#pragma warning disable EF1002 // checked against the pattern and the list above
        await db.Database.ExecuteSqlRawAsync($"DROP SCHEMA \"{schema}\" CASCADE", ct);
#pragma warning restore EF1002
        logger.LogWarning("Admin {AdminId} deleted the backup {Schema}", adminId, schema);
    }
}
