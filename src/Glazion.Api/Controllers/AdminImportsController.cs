using System.Text.Json;
using Glazion.Api.Contracts;
using Glazion.Api.Data;
using Glazion.Api.Domain;
using Glazion.Api.Infrastructure;
using Glazion.Api.Retrieval;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Glazion.Api.Controllers;

/// <summary>
/// Bulk ingestion for the labelled corpus. The importer never holds R2 credentials: it registers manifest rows,
/// receives API-generated object keys with presigned PUT URLs, uploads directly to R2, then asks the API to
/// verify each object with HeadObject and create the canonical image asset.
/// </summary>
[ApiController]
[Route("api/v2/admin/imports")]
[Authorize(Roles = "Admin")]
public sealed class AdminImportsController(GlazionDbContext db, IR2ObjectStorage storage, IImageIngestionService ingestion, LabCodeService labCodes, IOptions<UploadOptions> uploadOptions, IOptions<IngestionOptions> ingestionOptions, ILogger<AdminImportsController> logger) : ControllerBase
{
    private static readonly JsonSerializerOptions LabelJson = new(JsonSerializerDefaults.Web);

    [HttpPost]
    public async Task<ActionResult<ImportBatchResponse>> Create(CreateImportBatchRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 180) return BadRequest(new { error = "Batch name is required (max 180 characters)." });
        var variant = string.IsNullOrWhiteSpace(request.DefaultTileVariantKey) ? "white" : request.DefaultTileVariantKey.Trim().ToLowerInvariant();
        if (!ingestionOptions.Value.AllowedTileVariants.Contains(variant)) return BadRequest(new { error = $"Tile variant '{variant}' is not allowed." });
        var batch = new ImportBatch
        {
            Id = Guid.CreateVersion7(),
            Name = request.Name.Trim(),
            SourceLabel = string.IsNullOrWhiteSpace(request.SourceLabel) ? "curated_dataset" : request.SourceLabel.Trim(),
            DefaultTileVariantKey = variant,
            AutoApprove = request.AutoApprove,
            CreatedBy = User.GetUserId()
        };
        db.ImportBatches.Add(batch);
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Import batch {BatchId} created by {UserId} (autoApprove={AutoApprove})", batch.Id, batch.CreatedBy, batch.AutoApprove);
        return Created($"/api/v2/admin/imports/{batch.Id}", await ToResponseAsync(batch, cancellationToken));
    }

    [HttpGet]
    public Task<PagedResult<ImportBatchResponse>> List([FromQuery] string? cursor, [FromQuery] int? pageSize, CancellationToken cancellationToken) =>
        Paging.PageAsync(db.ImportBatches.AsNoTracking().OrderByDescending(x => x.CreatedAt).ThenBy(x => x.Id), cursor, pageSize, async batches =>
        {
            var result = new List<ImportBatchResponse>(batches.Count);
            foreach (var batch in batches) result.Add(await ToResponseAsync(batch, cancellationToken));
            return (IReadOnlyList<ImportBatchResponse>)result;
        }, cancellationToken);

    /// <summary>The import report: counts by record state and chemistry outcome.</summary>
    [HttpGet("{batchId:guid}")]
    public async Task<ActionResult<ImportBatchResponse>> Get(Guid batchId, CancellationToken cancellationToken)
    {
        var batch = await db.ImportBatches.AsNoTracking().SingleOrDefaultAsync(x => x.Id == batchId, cancellationToken);
        return batch is null ? NotFound() : Ok(await ToResponseAsync(batch, cancellationToken));
    }

    /// <summary>Records by external id, up to 500 per page.</summary>
    [HttpGet("{batchId:guid}/records")]
    public Task<PagedResult<ImportRecordResponse>> Records(Guid batchId, [FromQuery] ImportRecordStatus? status, [FromQuery] string? cursor, [FromQuery] int? pageSize, CancellationToken cancellationToken)
    {
        var query = db.ImportRecords.AsNoTracking().Include(x => x.ImageAsset).Where(x => x.BatchId == batchId);
        if (status is not null) query = query.Where(x => x.Status == status);
        return Paging.PageAsync(query.OrderBy(x => x.ExternalRecordId).ThenBy(x => x.Id), cursor, pageSize,
            rows => Task.FromResult<IReadOnlyList<ImportRecordResponse>>(rows.Select(ToRecordResponse).ToList()), cancellationToken, maxPageSize: 500);
    }

    [HttpPost("{batchId:guid}/close")]
    public async Task<IActionResult> Close(Guid batchId, CancellationToken cancellationToken)
    {
        var batch = await db.ImportBatches.SingleOrDefaultAsync(x => x.Id == batchId, cancellationToken);
        if (batch is null) return NotFound();
        if (batch.Status == ImportBatchStatus.Open) { batch.Status = ImportBatchStatus.Closed; batch.ClosedAt = DateTimeOffset.UtcNow; await db.SaveChangesAsync(cancellationToken); }
        return NoContent();
    }

    /// <summary>
    /// Registers up to MaxImportRecordsPerRequest manifest rows. Rows are validated (including chemistry) before
    /// anything is persisted so a bad manifest fails before any bytes move. Idempotent on externalRecordId:
    /// a row already registered gets a fresh URL; a row already uploaded gets its state and no URL.
    /// </summary>
    [HttpPost("{batchId:guid}/records")]
    public async Task<ActionResult<RegisterImportRecordsResponse>> Register(Guid batchId, RegisterImportRecordsRequest request, CancellationToken cancellationToken)
    {
        var options = ingestionOptions.Value;
        if (request.Records is null || request.Records.Count == 0) return BadRequest(new { error = "At least one record is required." });
        if (request.Records.Count > options.MaxImportRecordsPerRequest) return BadRequest(new { error = $"At most {options.MaxImportRecordsPerRequest} records per request." });
        var batch = await db.ImportBatches.SingleOrDefaultAsync(x => x.Id == batchId, cancellationToken);
        if (batch is null) return NotFound();
        if (batch.Status != ImportBatchStatus.Open) return Conflict(new { error = "Batch is closed." });

        var ids = request.Records.Select(x => x.ExternalRecordId).Where(x => x is not null).Distinct(StringComparer.Ordinal).ToArray();
        var existing = await db.ImportRecords.Where(x => x.BatchId == batchId && ids.Contains(x.ExternalRecordId)).ToDictionaryAsync(x => x.ExternalRecordId, StringComparer.Ordinal, cancellationToken);
        var recipeIds = request.Records.Where(x => x.RecipeId is not null).Select(x => x.RecipeId!.Value).Distinct().ToArray();
        var knownRecipes = recipeIds.Length == 0 ? [] : await db.Recipes.Where(x => recipeIds.Contains(x.Id) && x.IsActive).Select(x => x.Id).ToHashSetAsync(cancellationToken);
        var recipeCodes = request.Records.Select(x => LabCode.NormalizeRecipe(x.RecipeCode)).Where(x => x is not null).Distinct().ToArray();
        var recipesByCode = recipeCodes.Length == 0 ? new Dictionary<string, Guid>() : await db.Recipes.Where(x => x.Code != null && recipeCodes.Contains(x.Code) && x.IsActive).ToDictionaryAsync(x => x.Code!, x => x.Id, cancellationToken);

        var outcomes = new List<(ImportManifestRow Row, ImportRecord? Record, List<string> Errors, List<string> Warnings)>(request.Records.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in request.Records)
        {
            var errors = new List<string>();
            var warnings = new List<string>();
            if (!ObjectKeyPolicy.IsValidExternalRecordId(row.ExternalRecordId)) errors.Add("invalid_external_record_id");
            else if (!seen.Add(row.ExternalRecordId)) errors.Add("duplicate_in_request");
            if (string.IsNullOrWhiteSpace(row.FileName) || row.FileName.Length > 255) errors.Add("invalid_file_name");
            if (string.IsNullOrWhiteSpace(row.ContentType) || !uploadOptions.Value.AllowedContentTypes.Contains(row.ContentType, StringComparer.OrdinalIgnoreCase)) errors.Add("unsupported_content_type");
            if (row.SizeBytes <= 0 || row.SizeBytes > uploadOptions.Value.MaxBytes) errors.Add("size_out_of_bounds");
            if (row.Sha256 is not null && !IsSha256(row.Sha256)) errors.Add("invalid_sha256");
            var variant = string.IsNullOrWhiteSpace(row.TileVariantKey) ? batch.DefaultTileVariantKey : row.TileVariantKey.Trim().ToLowerInvariant();
            if (!options.AllowedTileVariants.Contains(variant)) errors.Add("invalid_tile_variant");
            var atmosphere = string.IsNullOrWhiteSpace(row.Atmosphere) ? null : row.Atmosphere.Trim().ToLowerInvariant();
            if (atmosphere is not null && !options.AllowedAtmospheres.Contains(atmosphere)) errors.Add("invalid_atmosphere");
            if (!options.TryNormalizeClayBody(row.ClayBody, out var clayBody)) errors.Add("invalid_clay_body");
            if (row.ConeMin is < -20 or > 20 || row.ConeMax is < -20 or > 20 || (row.ConeMin is not null && row.ConeMax is not null && row.ConeMin > row.ConeMax)) errors.Add("invalid_cone_range");
            Guid? resolvedRecipeId = row.RecipeId;
            if (row.RecipeId is { } recipeId && !knownRecipes.Contains(recipeId)) errors.Add("recipe_not_found");
            if (!string.IsNullOrWhiteSpace(row.RecipeCode))
            {
                var normalizedCode = LabCode.NormalizeRecipe(row.RecipeCode);
                if (normalizedCode is null) errors.Add("invalid_recipe_code");
                else if (!recipesByCode.TryGetValue(normalizedCode, out var byCode)) errors.Add("recipe_code_not_found");
                else if (row.RecipeId is not null && row.RecipeId != byCode) errors.Add("recipe_id_and_code_disagree");
                else resolvedRecipeId = byCode;
            }
            if (resolvedRecipeId is not null && row.Umf is not null) errors.Add("recipe_and_umf_both_supplied");
            UmfValidationResult? umf = null;
            if (row.Umf is { } umfElement && umfElement.ValueKind != JsonValueKind.Null)
            {
                umf = ChemistrySchema.ValidateUmf(umfElement);
                if (!umf.IsValid) errors.AddRange(umf.Errors);
                else warnings.AddRange(umf.Warnings);
            }

            if (errors.Count > 0) { outcomes.Add((row, null, errors, warnings)); continue; }
            if (existing.TryGetValue(row.ExternalRecordId, out var found)) { outcomes.Add((row, found, errors, warnings)); continue; }

            var record = new ImportRecord
            {
                Id = Guid.CreateVersion7(),
                BatchId = batch.Id,
                ExternalRecordId = row.ExternalRecordId,
                ObjectKey = ObjectKeyPolicy.ImportOriginal(batch.Id, row.ExternalRecordId, row.ContentType),
                FileName = Path.GetFileName(row.FileName),
                DeclaredContentType = row.ContentType.ToLowerInvariant(),
                DeclaredSizeBytes = row.SizeBytes,
                DeclaredSha256 = row.Sha256?.ToLowerInvariant(),
                TileVariantKey = variant,
                ConeMin = row.ConeMin,
                ConeMax = row.ConeMax,
                Atmosphere = atmosphere,
                ClayBody = clayBody,
                LabelsJson = row.Labels is null ? "{}" : JsonSerializer.Serialize(row.Labels, LabelJson),
                RecipeId = resolvedRecipeId,
                UmfJson = umf?.CanonicalJson,
                ChemistryWarningsJson = JsonSerializer.Serialize(warnings)
            };
            db.ImportRecords.Add(record);
            existing[record.ExternalRecordId] = record;
            outcomes.Add((row, record, errors, warnings));
        }

        // Rows exist before any URL is issued (UPLOAD-05 applied to imports).
        await db.SaveChangesAsync(cancellationToken);

        var registrations = new List<ImportRecordRegistration>(outcomes.Count);
        foreach (var (row, record, errors, warnings) in outcomes)
        {
            if (record is null) { registrations.Add(new ImportRecordRegistration(row.ExternalRecordId, false, null, null, null, errors, warnings)); continue; }
            PresignResponse? presign = null;
            if (record.Status == ImportRecordStatus.Registered)
            {
                var signed = await storage.CreatePutUrlAsync(record.ObjectKey, record.DeclaredContentType, new Dictionary<string, string> { ["import-record-id"] = record.Id.ToString("D"), ["schema-version"] = ObjectKeyPolicy.SchemaVersion }, cancellationToken);
                presign = new PresignResponse(record.Id, "PUT", signed.Url, signed.ObjectKey, signed.ExpiresAt, signed.Headers);
            }
            registrations.Add(new ImportRecordRegistration(record.ExternalRecordId, true, record.Status, record.ObjectKey, presign, errors, warnings));
        }
        return Ok(new RegisterImportRecordsResponse(batch.Id, registrations));
    }

    /// <summary>
    /// Verifies each object with HeadObject (bounded parallelism), then creates the image asset, chemistry link,
    /// and validation job in one transaction per request. A record whose object is missing or mismatched stays
    /// Registered so the importer can re-upload to the same key.
    /// </summary>
    [HttpPost("{batchId:guid}/records/complete")]
    public async Task<ActionResult<CompleteImportRecordsResponse>> Complete(Guid batchId, CompleteImportRecordsRequest request, CancellationToken cancellationToken)
    {
        var options = ingestionOptions.Value;
        if (request.ExternalRecordIds is null || request.ExternalRecordIds.Count == 0) return BadRequest(new { error = "At least one externalRecordId is required." });
        if (request.ExternalRecordIds.Count > options.MaxImportRecordsPerRequest) return BadRequest(new { error = $"At most {options.MaxImportRecordsPerRequest} records per request." });
        var batch = await db.ImportBatches.SingleOrDefaultAsync(x => x.Id == batchId, cancellationToken);
        if (batch is null) return NotFound();

        var ids = request.ExternalRecordIds.Distinct(StringComparer.Ordinal).ToArray();
        var records = await db.ImportRecords.Include(x => x.ImageAsset).Where(x => x.BatchId == batchId && ids.Contains(x.ExternalRecordId)).ToDictionaryAsync(x => x.ExternalRecordId, StringComparer.Ordinal, cancellationToken);

        var pending = records.Values.Where(x => x.Status == ImportRecordStatus.Registered).ToList();
        var heads = new Dictionary<Guid, StoredObject?>();
        using (var gate = new SemaphoreSlim(Math.Max(1, options.ImportHeadConcurrency)))
        {
            var tasks = pending.Select(async record =>
            {
                await gate.WaitAsync(cancellationToken);
                try { return (record.Id, Head: await storage.HeadAsync(record.ObjectKey, cancellationToken)); }
                finally { gate.Release(); }
            });
            foreach (var (id, head) in await Task.WhenAll(tasks)) heads[id] = head;
        }

        var results = new List<ImportRecordCompletion>(ids.Length);
        foreach (var externalId in ids)
        {
            if (!records.TryGetValue(externalId, out var record)) { results.Add(new ImportRecordCompletion(externalId, ImportRecordStatus.Registered, null, "record_not_found")); continue; }
            if (record.Status != ImportRecordStatus.Registered) { results.Add(new ImportRecordCompletion(externalId, record.Status, record.ImageAsset?.Id, null)); continue; }

            var stored = heads[record.Id];
            if (stored is null) { results.Add(new ImportRecordCompletion(externalId, record.Status, null, "object_not_found")); continue; }
            if (stored.Size != record.DeclaredSizeBytes) { results.Add(new ImportRecordCompletion(externalId, record.Status, null, "size_mismatch")); continue; }
            if (!string.IsNullOrWhiteSpace(stored.ContentType) && !string.Equals(stored.ContentType, record.DeclaredContentType, StringComparison.OrdinalIgnoreCase)) { results.Add(new ImportRecordCompletion(externalId, record.Status, null, "content_type_mismatch")); continue; }

            record.Status = ImportRecordStatus.Uploaded;
            record.UploadedAt = DateTimeOffset.UtcNow;
            var asset = await ingestion.RegisterImportRecordAsync(record, batch, stored, cancellationToken);
            results.Add(new ImportRecordCompletion(externalId, record.Status, asset.Id, null));
        }

        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" })
        {
            return Conflict(new { error = "A concurrent completion already registered one or more of these records. Retry the request." });
        }
        return Ok(new CompleteImportRecordsResponse(batch.Id, results));
    }

    private async Task<ImportBatchResponse> ToResponseAsync(ImportBatch batch, CancellationToken cancellationToken)
    {
        var byStatus = await db.ImportRecords.AsNoTracking().Where(x => x.BatchId == batch.Id).GroupBy(x => x.Status).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, cancellationToken);
        var assets = db.ImageAssets.AsNoTracking().Where(x => x.ImportRecord!.BatchId == batch.Id);
        var counts = new ImportBatchCounts(
            byStatus.Values.Sum(),
            byStatus.GetValueOrDefault(ImportRecordStatus.Registered),
            byStatus.GetValueOrDefault(ImportRecordStatus.Uploaded),
            byStatus.GetValueOrDefault(ImportRecordStatus.Validated),
            byStatus.GetValueOrDefault(ImportRecordStatus.NeedsReview),
            byStatus.GetValueOrDefault(ImportRecordStatus.Failed),
            await assets.CountAsync(x => x.ApprovalStatus == ApprovalStatus.Approved, cancellationToken),
            await db.IndexRecords.Where(r => r.EntityType == "image" && r.Indexable && r.IndexedAt != null && assets.Select(a => a.Id).Contains(r.EntityId)).Select(r => r.EntityId).Distinct().CountAsync(cancellationToken),
            await assets.CountAsync(x => x.ChemistryStatus == ChemistryStatus.Linked, cancellationToken),
            await assets.CountAsync(x => x.ChemistryStatus == ChemistryStatus.Missing, cancellationToken),
            await assets.CountAsync(x => x.ChemistryStatus == ChemistryStatus.Invalid, cancellationToken));
        return new ImportBatchResponse(batch.Id, batch.Name, batch.SourceLabel, batch.DefaultTileVariantKey, batch.AutoApprove, batch.Status, batch.CreatedBy, batch.CreatedAt, batch.ClosedAt, counts);
    }

    private static ImportRecordResponse ToRecordResponse(ImportRecord x) => new(x.Id, x.ExternalRecordId, x.ObjectKey, x.Status, x.FailureCode, x.FailureDetail, x.ImageAsset?.Id, x.ImageAsset?.QualityStatus, x.ImageAsset?.ApprovalStatus, x.ImageAsset?.ChemistryStatus, x.ChemistryWarningsJson, x.CreatedAt, x.UploadedAt);

    private static bool IsSha256(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);
}

/// <summary>Read side of the identity model: one call shows an image, its provenance, its recipe links, and its pinned UMF snapshot.</summary>
[ApiController]
[Route("api/v2/admin/images")]
[Authorize(Roles = "Admin")]
public sealed class AdminImagesController(GlazionDbContext db, IR2ObjectStorage storage, ImageApprovalService approvals, IOptions<IngestionOptions> ingestionOptions, MatchCheckService matchCheck,
    AiCopyService aiCopies) : ControllerBase
{
    // ---- AI copies (docs/features/ai-copies.md) ----

    /// <summary>The image's AI copies, newest first.</summary>
    [HttpGet("{imageId:guid}/ai-copies")]
    public async Task<ActionResult<IReadOnlyList<AiCopyDto>>> AiCopies(Guid imageId, CancellationToken cancellationToken) => Ok(await aiCopies.ListAsync(imageId, cancellationToken));

    /// <summary>Starts a copy (202); it is made in the background in a minute or so. instruction: anything to add for this copy; quality: low … max.</summary>
    [HttpPost("{imageId:guid}/ai-copies")]
    public async Task<ActionResult<AiCopyDto>> MakeAiCopy(Guid imageId, [FromBody(EmptyBodyBehavior = Microsoft.AspNetCore.Mvc.ModelBinding.EmptyBodyBehavior.Allow)] MakeAiCopyRequest? request,
        CancellationToken cancellationToken) => Accepted(await aiCopies.MakeAsync(imageId, request ?? new MakeAiCopyRequest(null, null), User.GetUserId(), cancellationToken));

    /// <summary>Shows the copy in place of the photo everywhere. The original is kept.</summary>
    [HttpPost("{imageId:guid}/ai-copies/{copyId:guid}/approve")]
    public async Task<ActionResult<AiCopyDto>> ApproveAiCopy(Guid imageId, Guid copyId, CancellationToken cancellationToken) =>
        Ok(await aiCopies.ApproveAsync(imageId, copyId, User.GetUserId(), cancellationToken));

    /// <summary>Discards the copy; if it was the one shown, the photo is shown again.</summary>
    [HttpPost("{imageId:guid}/ai-copies/{copyId:guid}/reject")]
    public async Task<ActionResult<AiCopyDto>> RejectAiCopy(Guid imageId, Guid copyId, CancellationToken cancellationToken) =>
        Ok(await aiCopies.RejectAsync(imageId, copyId, User.GetUserId(), cancellationToken));

    /// <summary>Shows the photo itself again; the copy is kept and can be approved later.</summary>
    [HttpPost("{imageId:guid}/ai-copies/revert")]
    public async Task<IActionResult> RevertAiCopy(Guid imageId, CancellationToken cancellationToken)
    {
        await aiCopies.RevertAsync(imageId, cancellationToken);
        return NoContent();
    }

    /// <summary>
    /// Match check's tile list: each image beside its recipe (materials, base, additions) and chemistry, newest first, so
    /// imported photos and new uploads can be checked against what they claim to show. Same filters as the photo list,
    /// plus source (Import or UserUpload). Up to 50 per page.
    /// </summary>
    [HttpGet("traces")]
    public async Task<ActionResult<PagedResult<ImageTrace>>> Traces([FromQuery] ApprovalStatus? approval, [FromQuery] ChemistryStatus? chemistry, [FromQuery] ImageSourceType? source,
        [FromQuery] string? recipeCode, [FromQuery] string? clayBody, [FromQuery] Guid? importBatchId, [FromQuery] string? participantCode, [FromQuery] string? cone,
        [FromQuery] string? q, [FromQuery] string? cursor, [FromQuery] int? pageSize, CancellationToken cancellationToken)
    {
        IQueryable<ImageAsset> query;
        try
        {
            query = AdminImageFilter.Apply(db.ImageAssets.AsNoTracking(),
                new AdminImageQuery(approval, null, chemistry, recipeCode, clayBody, importBatchId, participantCode, cone, null, q), ingestionOptions.Value);
        }
        catch (ApiProblemException ex) { return ApiProblems.ToResult(HttpContext, ex); }
        if (source is { } s) query = query.Where(x => x.SourceType == s);
        return await Paging.PageAsync(query.OrderByDescending(x => x.CreatedAt).ThenBy(x => x.Id).Select(x => x.Id), cursor, pageSize, async ids =>
        {
            var traces = await matchCheck.TraceAsync(ids, null, cancellationToken);
            return (IReadOnlyList<ImageTrace>)ids.Where(traces.ContainsKey).Select(id => traces[id]).ToList();
        }, cancellationToken, maxPageSize: 50);
    }

    /// <summary>Where the image came from and the recipe and UMF it carries (Match check), to confirm its chemistry link.</summary>
    [HttpGet("{imageId:guid}/trace")]
    public async Task<ActionResult<ImageTrace>> Trace(Guid imageId, CancellationToken cancellationToken) =>
        (await matchCheck.TraceAsync([imageId], null, cancellationToken)).GetValueOrDefault(imageId) is { } trace ? Ok(trace) : NotFound();

    /// <summary>
    /// Records the clay body on existing images (all of an import batch, or the listed ones). Images that already have
    /// one keep it unless onlyIfMissing is false. Approved images are re-synced to the index.
    /// </summary>
    [HttpPost("clay-body")]
    public async Task<ActionResult<SetClayBodyResponse>> SetClayBody(SetClayBodyRequest request, CancellationToken cancellationToken)
    {
        var options = ingestionOptions.Value;
        if (string.IsNullOrWhiteSpace(request.ClayBody) || !options.TryNormalizeClayBody(request.ClayBody, out var clayBody) || clayBody is null)
            throw ApiProblems.Validation("clayBody", $"Clay body must be one of {string.Join(", ", options.AllowedClayBodies)}.");
        if (request.ImageIds is { Count: > 5000 }) throw ApiProblems.Validation("imageIds", "At most 5000 image ids per call; use importBatchId or participantCode for larger sets.");
        var selector = new ClayBodyBackfill.Selector(request.ImageIds, request.ImportBatchId, request.ParticipantCode);
        return Ok(await ClayBodyBackfill.ApplyAsync(db, clayBody, selector, request.OnlyIfMissing ?? true, request.DryRun == true, cancellationToken));
    }

    /// <summary>Where the images came from (each import batch, each participant) and how many already have a clay body. Read-only.</summary>
    [HttpGet("sources")]
    public async Task<ActionResult<IReadOnlyList<ImageSourceGroup>>> Sources(CancellationToken cancellationToken) =>
        Ok(await ClayBodyBackfill.SourcesAsync(db, cancellationToken));

    /// <summary>Approve any image (upload or import) once validated; optional recipeId is attached and verified. Queues index sync.</summary>
    [HttpPost("{imageId:guid}/approve")]
    public async Task<IActionResult> Approve(Guid imageId, [FromBody(EmptyBodyBehavior = Microsoft.AspNetCore.Mvc.ModelBinding.EmptyBodyBehavior.Allow)] ApproveUploadRequest? request, CancellationToken cancellationToken)
    {
        var asset = await ImageApprovalService.ForApproval(db).SingleOrDefaultAsync(x => x.Id == imageId, cancellationToken);
        if (asset is null) return NotFound();
        var outcome = await approvals.ApproveAsync(asset, User.GetUserId(), request?.RecipeId, request?.Note, cancellationToken);
        return outcome.Succeeded ? NoContent() : StatusCode(outcome.StatusCode, new { error = outcome.Error });
    }

    [HttpPost("{imageId:guid}/reject")]
    public async Task<IActionResult> Reject(Guid imageId, [FromBody(EmptyBodyBehavior = Microsoft.AspNetCore.Mvc.ModelBinding.EmptyBodyBehavior.Allow)] RejectUploadRequest? request, CancellationToken cancellationToken)
    {
        var asset = await ImageApprovalService.ForApproval(db).SingleOrDefaultAsync(x => x.Id == imageId, cancellationToken);
        if (asset is null) return NotFound();
        var outcome = await approvals.RejectAsync(asset, User.GetUserId(), request?.Reason, cancellationToken);
        return outcome.Succeeded ? NoContent() : StatusCode(outcome.StatusCode, new { error = outcome.Error });
    }

    /// <summary>
    /// Creates display and thumbnail derivatives (and pixel metrics) for validated images that have none, or for the
    /// given IDs. <c>regenerate</c> redoes images that already have them (after a size or quality-policy change).
    /// </summary>
    [HttpPost("derivatives/backfill")]
    public async Task<ActionResult<BackfillDerivativesResponse>> BackfillDerivatives([FromBody(EmptyBodyBehavior = Microsoft.AspNetCore.Mvc.ModelBinding.EmptyBodyBehavior.Allow)] BackfillDerivativesRequest? request, CancellationToken cancellationToken)
    {
        var query = db.ImageAssets.AsNoTracking().Where(x => x.QualityStatus == QualityStatus.Passed || x.QualityStatus == QualityStatus.NeedsReview);
        if (request?.ImageIds is { Count: > 0 } ids) query = query.Where(x => ids.Contains(x.Id));
        else if (request?.Regenerate != true) query = query.Where(x => x.DisplayKey == null);
        var imageIds = await query.Select(x => x.Id).ToListAsync(cancellationToken);
        foreach (var id in imageIds) await ImageDerivatives.EnqueueAsync(db, id, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return Ok(new BackfillDerivativesResponse(imageIds.Count));
    }

    [HttpGet("{imageId:guid}")]
    public async Task<ActionResult<ImageAssetResponse>> Get(Guid imageId, CancellationToken cancellationToken)
    {
        var asset = await db.ImageAssets.AsNoTracking()
            .Include(x => x.ImportRecord).Include(x => x.CalculationResult).Include(x => x.Recipes).ThenInclude(x => x.Recipe)
            .SingleOrDefaultAsync(x => x.Id == imageId, cancellationToken);
        if (asset is null) return NotFound();
        return Ok(await ImageAssetMapper.ToResponseAsync(asset, storage, cancellationToken));
    }

    /// <summary>
    /// Newest first, up to 200 per page. Filters combine: approval, quality, chemistry, recipeCode (A03-B2-009 or a
    /// prefix such as A03-B2), clayBody (or "none"), importBatchId, participantCode, cone ("06" or 6), hasThumbnail,
    /// and q (file name, record id, recipe name or lab code).
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<PagedResult<ImageAssetResponse>>> List([FromQuery] ApprovalStatus? approval, [FromQuery] QualityStatus? quality, [FromQuery] ChemistryStatus? chemistry,
        [FromQuery] string? recipeCode, [FromQuery] string? clayBody, [FromQuery] Guid? importBatchId, [FromQuery] string? participantCode, [FromQuery] string? cone,
        [FromQuery] bool? hasThumbnail, [FromQuery] string? q, [FromQuery] string? cursor, [FromQuery] int? pageSize, CancellationToken cancellationToken)
    {
        IQueryable<ImageAsset> query;
        try
        {
            query = AdminImageFilter.Apply(db.ImageAssets.AsNoTracking().Include(x => x.ImportRecord).Include(x => x.CalculationResult).Include(x => x.Recipes).ThenInclude(x => x.Recipe),
                new AdminImageQuery(approval, quality, chemistry, recipeCode, clayBody, importBatchId, participantCode, cone, hasThumbnail, q), ingestionOptions.Value);
        }
        catch (ApiProblemException ex) { return ApiProblems.ToResult(HttpContext, ex); }
        return await Paging.PageAsync(query.OrderByDescending(x => x.CreatedAt).ThenBy(x => x.Id), cursor, pageSize, async assets =>
        {
            var result = new List<ImageAssetResponse>(assets.Count);
            foreach (var asset in assets) result.Add(await ImageAssetMapper.ToResponseAsync(asset, storage, cancellationToken));
            return (IReadOnlyList<ImageAssetResponse>)result;
        }, cancellationToken, maxPageSize: 200);
    }
}

public static class ImageAssetMapper
{
    public static async Task<ImageAssetResponse> ToResponseAsync(ImageAsset asset, IR2ObjectStorage storage, CancellationToken cancellationToken)
    {
        var recipes = asset.Recipes.OrderByDescending(r => r.Verified).ThenBy(r => r.CreatedAt).Select(r => new RecipeLinkResponse(r.RecipeId, r.Recipe?.Name ?? string.Empty, r.Recipe?.Code, r.RelationshipType, r.Verified)).ToList();
        // The same code everywhere: the signed download carries the lab code as its filename when one is linked.
        var downloadName = (recipes.FirstOrDefault(r => r.RecipeCode is not null)?.RecipeCode ?? asset.Id.ToString("D")) + "." + ObjectKeyPolicy.ExtensionFor(asset.DeclaredContentType);
        Uri? url = asset.QualityStatus == QualityStatus.Failed ? null : await storage.CreateGetUrlAsync(asset.OriginalKey, downloadName, cancellationToken);
        var chemistry = asset.CalculationResult is { } c ? new CalculationSnapshotResponse(c.Id, c.RecipeId, c.CalculationProfile, c.CalculationVersion, c.MaterialAnalysisVersion, c.ChemistrySchemaVersion, c.InputHash, c.FluxTotal, c.UmfJson, c.WarningsJson) : null;
        return new ImageAssetResponse(asset.Id, asset.SourceType, asset.UploadId, asset.ImportRecordId, asset.ImportRecord?.ExternalRecordId, asset.OwnerUserId,
            asset.TileVariantKey, asset.OriginalKey, asset.DisplayKey, asset.ThumbnailKey, asset.Format, asset.Width, asset.Height, asset.SizeBytes, asset.Sha256,
            asset.QualityStatus, asset.QualityReasonsJson, asset.QualityPolicyVersion, asset.ApprovalStatus, asset.ApprovedAt,
            asset.ChemistryStatus, chemistry, recipes, asset.DuplicateOfImageId, asset.ConeMin, asset.ConeMax, asset.Atmosphere, asset.LabelsJson,
            asset.SourceVersion, asset.CreatedAt, asset.ValidatedAt, url,
            url is not null && asset.DisplayKey is not null ? await storage.CreateGetUrlAsync(asset.DisplayKey, cancellationToken) : null,
            url is not null && asset.ThumbnailKey is not null ? await storage.CreateGetUrlAsync(asset.ThumbnailKey, cancellationToken) : null,
            asset.QualityMetricsJson is null ? null : System.Text.Json.JsonDocument.Parse(asset.QualityMetricsJson).RootElement.Clone(), asset.DerivativesCreatedAt, asset.ClayBody,
            asset.DisplayCopyId);
    }
}
