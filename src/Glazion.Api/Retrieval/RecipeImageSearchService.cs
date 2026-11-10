using Glazion.Api.Contracts;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Glazion.Api.Chemistry;
using Glazion.Api.Data;
using Glazion.Api.Domain;
using Glazion.Api.Indexing;
using Glazion.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Glazion.Api.Retrieval;

/// <summary>
/// Recipe/UMF → ranked real tile images (PRD §5.6, §11.3; plan §10–11). Steps:
/// 1 validate and canonicalise; 2 hard approval/visibility/variant/strict-firing filters; 3–4 chemistry and text KNN in
/// parallel; 6 union; 7 load authoritative PostgreSQL rows; 8 drop stale/inaccessible; 9 hybrid rank; 10 top K with
/// explanations and short-lived signed URLs. The run is persisted with every version that influenced it.
/// Vector results are candidates only and are never trusted for authorisation.
/// </summary>
public sealed class RecipeImageSearchService(
    GlazionDbContext db,
    IVectorIndex vectorIndex,
    ITextEmbeddingProvider embeddings,
    IR2ObjectStorage storage,
    IOptions<RetrievalOptions> retrievalOptions,
    IOptions<IngestionOptions> ingestionOptions,
    ChemistryDataProvider chemistry,
    ILogger<RecipeImageSearchService> logger)
{
    public const string ResultTypeRealTile = "real_tile";
    public const string NoReliableMatch = "no_reliable_match";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly RetrievalOptions _options = retrievalOptions.Value;
    private readonly IngestionOptions _ingestion = ingestionOptions.Value;

    private sealed record CanonicalQuery(
        string Type, IReadOnlyDictionary<string, decimal> Umf, string? CalculationProfile, string? CalculationVersion,
        Guid? RecipeId, string? RecipeCode, Guid? CalculationResultId,
        FiringQuery Firing, bool Strict,
        IReadOnlyList<string> TileVariants, IReadOnlyList<string> Surface, IReadOnlyList<string> Opacity, IReadOnlyList<string> ColourFamily,
        IReadOnlyList<string> ClayBodies, int TopK, bool IncludeRecipeMetadata, IReadOnlyList<string> ChemistryWarnings, string? OxideWeightPercentJson,
        IReadOnlyList<Colourant>? Colourants, string? Name = null, IReadOnlyList<SearchedMaterial>? Materials = null,
        IReadOnlyList<ColorantMaterial>? ColorantMaterials = null, IReadOnlyDictionary<string, double>? ColorantOxides = null)
    {
        /// <summary>Both kinds of colorant: metallic oxides from the UMF as sent (FeO and V2O5 included), stains when known.</summary>
        public ColorantProfile ColorantProfile => new(ColorantOxides ?? new Dictionary<string, double>(), Colourants);
    }

    public async Task<RecipeImageSearchResponse> SearchAsync(RecipeImageSearchRequest request, SearchRequester requester, string? idempotencyKey, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var query = await CanonicaliseAsync(request, requester, cancellationToken);
        var inputHash = HashQuery(query);

        if (!string.IsNullOrWhiteSpace(idempotencyKey))
        {
            if (idempotencyKey.Length > 128) throw Validation("Idempotency-Key", "Idempotency-Key must be at most 128 characters.");
            var previous = await db.SearchRuns.AsNoTracking().SingleOrDefaultAsync(x => x.RequesterUserId == requester.UserId && x.IdempotencyKey == idempotencyKey, cancellationToken);
            if (previous is not null)
            {
                if (previous.InputHash != inputHash)
                    throw new ApiProblemException(422, "idempotency_key_reused", "Idempotency key reused", "This Idempotency-Key was already used for a different search. Use a new key for a new query.");
                return await RehydrateAsync(previous, requester, cancellationToken);
            }
        }

        var warnings = new List<string>(query.ChemistryWarnings);

        // Query representations, built exactly as index documents are (same schema and canonical text versions).
        var chemistryVector = ChemistryVectorBuilder.Build(query.Umf);
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        if (query.Surface.Count == 1) labels["surface"] = query.Surface[0];
        if (query.Opacity.Count == 1) labels["opacity"] = query.Opacity[0];
        if (query.ColourFamily.Count == 1) labels["colourFamily"] = query.ColourFamily[0];
        var queryText = CanonicalTextBuilder.Build(new CanonicalTextInput(query.CalculationProfile, query.Umf, query.Firing.ConeMin, query.Firing.ConeMax, query.Firing.Atmosphere,
            query.TileVariants.Count == 1 ? query.TileVariants[0] : string.Empty, labels));

        var filter = BuildVectorFilter(query, _options.ConeTolerance);
        var poolSize = Math.Min(_options.MaxCandidates, Math.Max(_options.MinCandidates, query.TopK * _options.CandidateMultiplier));
        var chemistryNamespace = VectorNamespaces.ImageChemistry(ChemistrySchema.Version);
        var textNamespace = VectorNamespaces.ImageText(embeddings.Version);

        float[]? textVector = null;
        try { textVector = (await embeddings.EmbedAsync([queryText], cancellationToken))[0]; }
        catch (VectorIndexException ex) { logger.LogWarning(ex, "Query embedding failed; continuing with chemistry only"); }

        var chemistryTask = QuerySafeAsync(new VectorQuery(chemistryNamespace, chemistryVector.Normalized(), poolSize, filter, IncludeMetadata: false), cancellationToken);
        var textTask = textVector is null ? Task.FromResult<IReadOnlyList<VectorMatch>?>(null) : QuerySafeAsync(new VectorQuery(textNamespace, textVector, poolSize, filter, IncludeMetadata: false), cancellationToken);
        // Stains do not move the UMF, so hundreds of variations of one base share a chemistry vector and the plain
        // KNN returns an arbitrary few of them. A second chemistry query restricted to tiles containing every one of
        // the query's stains makes sure those are candidates (every, not any: an unanalysed common material, such as
        // a whiting with no analysis, would otherwise match nearly every tile). A third keeps the closest unstained
        // tiles, the base glaze itself, in view for the same reason.
        var colourantTask = query.Colourants is { Count: > 0 } qc
            ? QuerySafeAsync(new VectorQuery(chemistryNamespace, chemistryVector.Normalized(), poolSize,
                VectorFilter.All([filter, .. qc.Select(c => VectorFilter.AnyOf(VectorMetadataKeys.Colourants, [c.MaterialId]))]), IncludeMetadata: false), cancellationToken)
            : Task.FromResult<IReadOnlyList<VectorMatch>?>(null);
        var unstainedTask = query.Colourants is not null
            ? QuerySafeAsync(new VectorQuery(chemistryNamespace, chemistryVector.Normalized(), poolSize, VectorFilter.All(filter, VectorFilter.Eq(VectorMetadataKeys.Stained, false)), IncludeMetadata: false), cancellationToken)
            : Task.FromResult<IReadOnlyList<VectorMatch>?>(null);
        await Task.WhenAll(chemistryTask, textTask, colourantTask, unstainedTask);
        var chemistryMatches = chemistryTask.Result;
        var textMatches = textTask.Result;
        var colourantMatches = colourantTask.Result;
        var unstainedMatches = unstainedTask.Result;
        if (chemistryMatches is null && textMatches is null)
            throw new ApiProblemException(503, "vector_index_unavailable", "Search temporarily unavailable", "The vector index could not be queried. Retry shortly.");
        if (chemistryMatches is null) warnings.Add("chemistry_search_unavailable");
        if (textMatches is null) warnings.Add("text_search_unavailable");

        // Union candidate IDs, remembering which namespaces produced each one and the text score where known.
        var matchedIn = new Dictionary<Guid, HashSet<string>>();
        var textScores = new Dictionary<Guid, double>();
        void Collect(IReadOnlyList<VectorMatch>? matches, string ns, bool isText)
        {
            if (matches is null) return;
            foreach (var m in matches)
            {
                if (!VectorIds.TryParse(m.Id, out var type, out var imageId, out _) || type != EntityTypes.Image) continue;
                if (!matchedIn.TryGetValue(imageId, out var set)) matchedIn[imageId] = set = new HashSet<string>(StringComparer.Ordinal);
                set.Add(ns);
                if (isText) textScores[imageId] = Math.Max(0, m.Score);
            }
        }
        Collect(chemistryMatches, chemistryNamespace, false);
        Collect(textMatches, textNamespace, true);
        Collect(colourantMatches, chemistryNamespace, false);
        Collect(unstainedMatches, chemistryNamespace, false);
        var candidateIds = matchedIn.Keys.ToList();

        // Authoritative rows: approval and quality come from PostgreSQL, never from vector metadata.
        var assets = candidateIds.Count == 0 ? [] : await db.ImageAssets.AsNoTracking()
            .Include(x => x.CalculationResult).Include(x => x.Recipes).ThenInclude(x => x.Recipe)
            .Where(x => candidateIds.Contains(x.Id) && x.ApprovalStatus == ApprovalStatus.Approved && x.QualityStatus != QualityStatus.Failed)
            .ToListAsync(cancellationToken);
        var liveNamespaces = candidateIds.Count == 0 ? [] : (await db.IndexRecords.AsNoTracking()
            .Where(x => x.EntityType == EntityTypes.Image && candidateIds.Contains(x.EntityId) && x.Indexable && x.SupersededAt == null && x.IndexedAt != null)
            .Select(x => new { x.EntityId, x.VectorNamespace }).ToListAsync(cancellationToken))
            .GroupBy(x => x.EntityId).ToDictionary(g => g.Key, g => g.Select(x => x.VectorNamespace).ToHashSet(StringComparer.Ordinal));

        var eligible = new List<ImageAsset>();
        var staleOrInaccessible = candidateIds.Count - assets.Count;
        foreach (var asset in assets)
        {
            // A match from a namespace with no live manifest row is a stale vector (superseded or never confirmed).
            var live = liveNamespaces.GetValueOrDefault(asset.Id);
            if (live is null || !matchedIn[asset.Id].Any(live.Contains)) { staleOrInaccessible++; continue; }
            if (!PassesHardFilters(asset, query, _options.ConeTolerance)) { staleOrInaccessible++; continue; }
            eligible.Add(asset);
        }

        // Text similarity for candidates that only the chemistry KNN found: fetch their stored text vectors.
        if (textVector is not null && textMatches is not null)
        {
            var missing = eligible.Where(a => !textScores.ContainsKey(a.Id) && liveNamespaces[a.Id].Contains(textNamespace)).Select(a => VectorIds.Image(a.Id, embeddings.Version)).ToList();
            if (missing.Count > 0)
            {
                try
                {
                    var fetched = await vectorIndex.FetchAsync(textNamespace, missing, cancellationToken);
                    foreach (var (id, record) in fetched)
                        if (VectorIds.TryParse(id, out _, out var imageId, out _) && record.Values.Length == textVector.Length)
                            textScores[imageId] = Math.Max(0, ChemistryVectorBuilder.CosineSimilarity(textVector, record.Values));
                }
                catch (VectorIndexException ex) { logger.LogWarning(ex, "Text vector fetch failed; affected candidates rank without text similarity"); }
            }
        }

        ChemistryData? data = chemistry.TryGet(out var loaded, out _) ? loaded : null;
        var curation = chemistry.Curation;
        var scored = eligible.Select(a => Score(a, query, textScores.TryGetValue(a.Id, out var t) ? t : null, requester, data, curation)).ToList();
        var ordered = scored.Where(x => x.Item.Score >= _options.MinimumConfidence)
            .OrderByDescending(x => x.Item.Score)
            .ThenByDescending(x => x.VerifiedRecipe)
            .ThenByDescending(x => x.QualityPassed)
            .ThenByDescending(x => x.Item.ScoreBreakdown.ChemistryCoverage ?? 0)
            .ThenBy(x => x.Item.ImageId)
            .Select((x, i) => x with { Item = x.Item with { Rank = i + 1 } })
            .ToList();
        var ranked = ordered.Take(query.TopK).Select(x => x.Item).ToList();
        if (ranked.Count == 0) warnings.Add(NoReliableMatch);
        // Suggestions: the best image of each other recipe, so the list shows different glazes rather than more
        // photographs of the closest one.
        var closestGroup = ordered.Count > 0 ? ordered[0].GroupKey : (Guid?)null;
        var similar = ordered.Where(x => x.GroupKey != closestGroup).GroupBy(x => x.GroupKey).Select(g => g.First().Item)
            .Take(Math.Max(0, _options.SimilarCount)).ToList();
        // The chemistry each shown tile was compared on, so Match check can show it even after the tile is re-calculated.
        var calculations = eligible.ToDictionary(a => a.Id, a => a.CalculationResultId);
        var pinned = ranked.Concat(similar).Select(x => x.ImageId).Distinct()
            .Select(id => new { imageId = id, calculationResultId = calculations.GetValueOrDefault(id) }).ToList();

        var run = new SearchRun
        {
            Id = Guid.CreateVersion7(),
            RequesterUserId = requester.UserId,
            QueryType = query.Type,
            InputHash = inputHash,
            IdempotencyKey = string.IsNullOrWhiteSpace(idempotencyKey) ? null : idempotencyKey,
            QuerySnapshotJson = JsonSerializer.Serialize(new
            {
                similar,
                pinned,
                // What the person typed, as typed (Match check shows it beside each result); not part of the hash.
                input = new { name = query.Name, materials = query.Materials },
                query = new { query.Type, umf = JsonSerializer.Deserialize<JsonElement>(ChemistrySchema.CanonicalJson(query.Umf)), colourants = query.Colourants, colorantMaterials = query.ColorantMaterials, query.CalculationProfile, query.CalculationVersion, query.RecipeId, query.RecipeCode, query.CalculationResultId, firing = query.Firing, query.Strict, query.TopK, query.IncludeRecipeMetadata, oxideWeightPercent = query.OxideWeightPercentJson is null ? (JsonElement?)null : JsonSerializer.Deserialize<JsonElement>(query.OxideWeightPercentJson) },
                canonicalText = queryText,
                versions = new { chemistrySchema = ChemistrySchema.Version, colourants = Colourants.Version, canonicalText = CanonicalTextBuilder.Version, embeddingModel = embeddings.Model, embeddingVersion = embeddings.Version, ranker = _options.RankerVersion, vectorProvider = vectorIndex.ProviderName },
                ranker = new { _options.Weights, _options.MinimumConfidence, _options.HighConfidence, _options.MediumConfidence, _options.ConeTolerance, _options.ChemistryCosineShare, _options.MinimumChemistryCoverage, poolSize },
                diagnostics = new { chemistryMatches = chemistryMatches?.Count, textMatches = textMatches?.Count, colourantMatches = colourantMatches?.Count, unstainedMatches = unstainedMatches?.Count, staleOrInaccessible, scored = scored.Count }
            }, Json),
            FiltersJson = JsonSerializer.Serialize(new { tileVariantKeys = query.TileVariants, clayBodies = query.ClayBodies, surface = query.Surface, opacity = query.Opacity, colourFamily = query.ColourFamily, strictFiring = query.Strict }, Json),
            CandidateCount = candidateIds.Count,
            ResultCount = ranked.Count,
            RankerVersion = _options.RankerVersion,
            ResultsJson = JsonSerializer.Serialize(ranked, Json),
            WarningsJson = JsonSerializer.Serialize(warnings, Json)
        };
        stopwatch.Stop();
        run.LatencyMs = (int)stopwatch.ElapsedMilliseconds;
        db.SearchRuns.Add(run);
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" } && run.IdempotencyKey is not null)
        {
            // A concurrent request with the same key won; return its run.
            db.ChangeTracker.Clear();
            var winner = await db.SearchRuns.AsNoTracking().SingleAsync(x => x.RequesterUserId == requester.UserId && x.IdempotencyKey == run.IdempotencyKey, cancellationToken);
            return await RehydrateAsync(winner, requester, cancellationToken);
        }

        logger.LogInformation("Search {SearchId} ({Type}) candidates={Candidates} eligible={Eligible} returned={Returned} latencyMs={Latency}", run.Id, query.Type, candidateIds.Count, eligible.Count, ranked.Count, run.LatencyMs);
        var signed = await SignAsync(ranked, cancellationToken);
        var signedSimilar = await SignAsync(similar, cancellationToken);
        return new RecipeImageSearchResponse(run.Id, new SearchQuerySummary(query.Type, "sha256:" + inputHash, query.CalculationProfile, query.RecipeCode, query.Umf, query.Colourants, query.ColorantMaterials),
            new SearchRankerSummary(_options.RankerVersion, candidateIds.Count, signed.Count, _options.MinimumConfidence), signed, warnings, run.CreatedAt,
            signed.FirstOrDefault(), signedSimilar);
    }

    /// <summary>Returns a stored run with freshly signed URLs. Items whose image is no longer approved are withheld (re-authorised on every read).</summary>
    public async Task<RecipeImageSearchResponse?> GetAsync(Guid searchId, SearchRequester requester, CancellationToken cancellationToken)
    {
        var run = await db.SearchRuns.AsNoTracking().SingleOrDefaultAsync(x => x.Id == searchId, cancellationToken);
        if (run is null || (run.RequesterUserId != requester.UserId && !requester.IsAdmin)) return null;
        return await RehydrateAsync(run, requester, cancellationToken);
    }

    public async Task<SearchFeedbackResponse> RecordFeedbackAsync(SearchFeedbackRequest request, SearchRequester requester, CancellationToken cancellationToken)
    {
        var value = request.Feedback?.Trim().ToLowerInvariant();
        if (value is not ("relevant" or "not_relevant")) throw Validation("feedback", "Feedback must be 'relevant' or 'not_relevant'.");
        var note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim();
        if (note is { Length: > 1000 }) throw Validation("note", "Note must be at most 1000 characters.");

        var run = await db.SearchRuns.AsNoTracking().SingleOrDefaultAsync(x => x.Id == request.SearchId && x.RequesterUserId == requester.UserId, cancellationToken)
                  ?? throw new ApiProblemException(404, "search_not_found", "Search not found", "No search with this ID belongs to you.");
        var items = JsonSerializer.Deserialize<List<SearchResultItem>>(run.ResultsJson, Json) ?? [];
        items.AddRange(SimilarFromSnapshot(run.QuerySnapshotJson));
        if (items.All(x => x.ImageId != request.ImageId))
            throw new ApiProblemException(422, "image_not_in_results", "Image not in results", "Feedback can only be given on an image returned by this search.");

        var existing = await db.SearchFeedback.SingleOrDefaultAsync(x => x.SearchRunId == run.Id && x.ImageId == request.ImageId && x.RequesterUserId == requester.UserId, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        if (existing is null)
        {
            existing = new SearchFeedback { Id = Guid.CreateVersion7(), SearchRunId = run.Id, ImageId = request.ImageId, RequesterUserId = requester.UserId, Feedback = value, Note = note, CreatedAt = now, UpdatedAt = now };
            db.SearchFeedback.Add(existing);
        }
        else { existing.Feedback = value; existing.Note = note; existing.UpdatedAt = now; }
        await db.SaveChangesAsync(cancellationToken);
        return new SearchFeedbackResponse(existing.Id, run.Id, existing.ImageId, existing.Feedback, existing.Note, existing.CreatedAt, existing.UpdatedAt);
    }

    // ---- Canonicalisation (R2I-01, R2I-02) ----

    private async Task<CanonicalQuery> CanonicaliseAsync(RecipeImageSearchRequest request, SearchRequester requester, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var type = request.Query?.Type?.Trim().ToLowerInvariant();
        if (type is not ("umf" or "recipe" or "raw_recipe")) errors["query.type"] = ["query.type must be 'umf', 'recipe', or 'raw_recipe'."];

        var topK = request.TopK ?? _options.DefaultTopK;
        if (topK < 1 || topK > _options.MaxTopK) errors["topK"] = [$"topK must be between 1 and {_options.MaxTopK}."];

        var firing = request.Firing;
        var atmosphere = string.IsNullOrWhiteSpace(firing?.Atmosphere) ? null : firing.Atmosphere.Trim().ToLowerInvariant();
        if (atmosphere is not null && !_ingestion.AllowedAtmospheres.Contains(atmosphere)) errors["firing.atmosphere"] = [$"Atmosphere must be one of {string.Join(", ", _ingestion.AllowedAtmospheres)}."];
        if (firing?.ConeMin is < -20 or > 20 || firing?.ConeMax is < -20 or > 20) errors["firing.cone"] = ["Cone values must be between -20 and 20 (negative values are 0x cones, e.g. -6 for cone 06)."];
        if (firing?.ConeMin is { } cmin && firing.ConeMax is { } cmax && cmin > cmax) errors["firing.cone"] = ["coneMin must not exceed coneMax."];
        var coneMin = firing?.ConeMin ?? firing?.ConeMax;
        var coneMax = firing?.ConeMax ?? firing?.ConeMin;
        if (firing?.Strict == true && coneMin is null && atmosphere is null) errors["firing.strict"] = ["Strict firing requires a cone or an atmosphere."];
        // Strict unless an admin has turned it off (SearchSettings); a search with no cone or atmosphere has nothing to filter on.
        var strict = (firing?.Strict == true || await SearchSettings.StrictFiringAsync(db, cancellationToken)) && (coneMin is not null || atmosphere is not null);

        var variants = Normalise(request.Filters?.TileVariantKeys);
        var unknownVariants = variants.Where(v => !_ingestion.AllowedTileVariants.Contains(v)).ToList();
        if (unknownVariants.Count > 0) errors["filters.tileVariantKeys"] = [$"Unknown tile variant(s): {string.Join(", ", unknownVariants)}."];
        var clayBodies = new List<string>();
        foreach (var value in request.Filters?.ClayBodies ?? [])
        {
            if (!_ingestion.TryNormalizeClayBody(value, out var clayBody)) { errors["filters.clayBodies"] = [$"Clay body must be one of {string.Join(", ", _ingestion.AllowedClayBodies)}."]; break; }
            if (clayBody is not null && !clayBodies.Contains(clayBody)) clayBodies.Add(clayBody);
        }
        clayBodies.Sort(StringComparer.Ordinal);

        IReadOnlyDictionary<string, decimal> umf = new Dictionary<string, decimal>();
        var chemistryWarnings = new List<string>();
        string? profile = null, version = null, recipeCode = null, oxideWeightPercentJson = null;
        IReadOnlyList<Colourant>? colourants = null;
        IReadOnlyList<SearchedMaterial>? typed = null;
        IReadOnlyList<ColorantMaterial>? colorantMaterials = null;
        IReadOnlyDictionary<string, double>? colorantOxides = null;
        Guid? recipeId = null, calculationResultId = null;

        if (type == "umf")
        {
            if (request.Query!.Umf is not { ValueKind: JsonValueKind.Object } umfElement) errors["query.umf"] = ["query.umf must be an object of oxide → UMF value."];
            else
            {
                var validation = ChemistrySchema.ValidateUmf(umfElement);
                if (!validation.IsValid) errors["query.umf"] = validation.Errors.ToArray();
                else { umf = validation.Umf; chemistryWarnings.AddRange(validation.Warnings); colorantOxides = ColorantMatch.OxidesOf(umfElement); }
            }
            profile = Trim(request.Query.CalculationProfile, 64);
            // The web and mobile apps send "flux-to-1" (their unityMode name) for the calculator that the backend runs
            // as glazion-umf-v1; same arithmetic, so it is the same profile.
            if (profile is not null && GlazeChemistry.ProfileAliases.Contains(profile)) profile = GlazeChemistry.CalculationProfile;
            version = Trim(request.Query.CalculationVersion, 64);
            if (request.Query.OxideWeightPercent is { ValueKind: JsonValueKind.Object } owp) oxideWeightPercentJson = owp.GetRawText();
        }
        else if (type == "raw_recipe")
        {
            // R2I-02 / plan §10.3: every raw material must resolve before search. The calculator is the client's,
            // ported verbatim; unlike its interactive use, search refuses unresolved rows instead of skipping them.
            var materials = request.Query!.Materials ?? [];
            if (materials.Count == 0) errors["query.materials"] = ["query.materials must list at least one material row."];
            if (materials.Count > 200) errors["query.materials"] = ["At most 200 material rows."];
            var units = materials.Select(m => m.Unit?.Trim()).Where(u => !string.IsNullOrEmpty(u)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (units.Count > 1) errors["query.materials"] = [$"Rows use different units ({string.Join(", ", units)}). Use one unit for every row."];
            if (errors.Count > 0) throw ValidationProblem(errors);

            if (!chemistry.TryGet(out var data, out var dataError))
                throw new ApiProblemException(503, "chemistry_data_unavailable", "UMF calculator unavailable", dataError ?? "Chemistry data is not loaded.");
            var rows = UmfCalculationService.WithAliases(materials.Select(m => new RecipeRowInput(m.RawName, m.Amount is { } a ? JsonSerializer.SerializeToElement(a) : null, string.IsNullOrWhiteSpace(m.MaterialId) ? null : m.MaterialId.Trim())).ToList(), data, chemistry.Curation);
            var resolutions = UmfCalculationService.Resolve(data, rows, chemistry.Curation);
            if (resolutions.Any(r => r.Status != "resolved"))
                throw new ApiProblemException(422, "materials_unresolved", "Materials could not be resolved",
                    "Every material must match a name in the materials list, with an amount, before chemistry can be calculated. Rows are never dropped silently.",
                    new Dictionary<string, object?> { ["resolutions"] = resolutions });
            var calculated = GlazeChemistry.CalculateRecipe(rows, data)
                             ?? throw new ApiProblemException(422, "recipe_chemistry_unavailable", "No chemistry", "The recipe's amounts add up to zero or less.");
            if (calculated.Chem.FluxSum <= GlazeChemistry.Zero)
                throw new ApiProblemException(422, "zero_flux", "UMF undefined", GlazeChemistry.NoFluxWarning, new Dictionary<string, object?> { ["warnings"] = calculated.Chem.Warnings });
            using var umfDoc = JsonDocument.Parse(JsonSerializer.Serialize(calculated.Chem.Umf));
            var validation = ChemistrySchema.ValidateUmf(umfDoc.RootElement);
            if (!validation.IsValid)
                throw new ApiProblemException(422, "recipe_chemistry_invalid", "Calculated chemistry is invalid", "The calculated UMF does not satisfy the oxide schema.", new Dictionary<string, object?> { ["errors"] = validation.Errors });
            umf = validation.Umf;
            colorantOxides = ColorantMatch.OxidesOf(umfDoc.RootElement);
            chemistryWarnings.AddRange(calculated.Chem.Warnings);
            chemistryWarnings.AddRange(validation.Warnings);
            profile = GlazeChemistry.CalculationProfile;
            version = GlazeChemistry.CalculationVersion;
            oxideWeightPercentJson = JsonSerializer.Serialize(calculated.Chem.AnalysisPct);
            colourants = Colourants.FromLines(calculated.Lines, data, chemistry.Curation);
            colorantMaterials = Colourants.MaterialsFromLines(calculated.Lines, data, chemistry.Curation);
            chemistryWarnings.AddRange(Colourants.WithoutAnalysis(calculated.Lines, data).Select(Colourants.NoAnalysisWarning));
            typed = materials.Select((m, i) => new SearchedMaterial(Trim(m.RawName, 200), resolutions[i].MaterialId, resolutions[i].MaterialName, m.Amount, Trim(m.Unit, 16))).ToList();
        }
        else if (type == "recipe")
        {
            Recipe? recipe = null;
            if (request.Query!.RecipeId is { } id) recipe = await db.Recipes.AsNoTracking().Include(x => x.BaseGlaze).SingleOrDefaultAsync(x => x.Id == id && x.IsActive, cancellationToken);
            else if (LabCode.NormalizeRecipe(request.Query.RecipeCode) is { } code) recipe = await db.Recipes.AsNoTracking().Include(x => x.BaseGlaze).SingleOrDefaultAsync(x => x.Code == code && x.IsActive, cancellationToken);
            else errors["query.recipeId"] = ["Provide query.recipeId or a lab code in query.recipeCode (e.g. A03-B2-009)."];

            if (errors.Count == 0)
            {
                if (recipe is null || !await CanUseRecipeAsync(recipe, requester, cancellationToken))
                    throw new ApiProblemException(404, "recipe_not_found", "Recipe not found", "The recipe does not exist or is not visible to you.");
                var snapshot = await db.RecipeCalculationResults.AsNoTracking().Where(x => x.RecipeId == recipe.Id).OrderByDescending(x => x.CreatedAt).FirstOrDefaultAsync(cancellationToken);
                // No stored snapshot yet: calculate from the recipe's own materials for this search (nothing is stored).
                snapshot ??= TransientSnapshot(recipe)
                    ?? throw new ApiProblemException(422, "recipe_chemistry_unavailable", "Recipe has no chemistry",
                        $"Recipe {recipe.Code ?? recipe.Id.ToString("D")} has no calculation snapshot and no material rows to calculate from. Search with its materials or its UMF instead.");
                using var document = JsonDocument.Parse(snapshot.UmfJson);
                var validation = ChemistrySchema.ValidateUmf(document.RootElement);
                if (!validation.IsValid)
                    throw new ApiProblemException(422, "recipe_chemistry_invalid", "Recipe chemistry is invalid", "The recipe's stored calculation snapshot does not satisfy the oxide schema.", new Dictionary<string, object?> { ["errors"] = validation.Errors });
                umf = validation.Umf;
                colorantOxides = ColorantMatch.OxidesOf(document.RootElement);
                chemistryWarnings.AddRange(validation.Warnings);
                profile = snapshot.CalculationProfile;
                version = snapshot.CalculationVersion;
                recipeId = recipe.Id;
                recipeCode = recipe.Code;
                calculationResultId = snapshot.Id == Guid.Empty ? null : snapshot.Id;
                if (snapshot.Id == Guid.Empty) chemistryWarnings.Add("calculated_for_this_search");
                if (chemistry.TryGet(out var recipeData, out _))
                {
                    var lines = Colourants.LinesFromSnapshot(snapshot);
                    if (lines is null)
                        try { lines = GlazeChemistry.CalculateRecipe(UmfCalculationService.WithAliases(UmfCalculationService.RowsFromRecipe(recipe), recipeData, chemistry.Curation), recipeData)?.Lines; }
                        catch (ApiProblemException) { lines = null; }
                    if (lines is not null)
                    {
                        colourants = Colourants.FromLines(lines, recipeData, chemistry.Curation);
                        colorantMaterials = Colourants.MaterialsFromLines(lines, recipeData, chemistry.Curation);
                        chemistryWarnings.AddRange(Colourants.WithoutAnalysis(lines, recipeData).Select(Colourants.NoAnalysisWarning));
                    }
                }
            }
        }

        if (errors.Count > 0) throw ValidationProblem(errors);
        return new CanonicalQuery(type!, umf, profile, version, recipeId, recipeCode, calculationResultId,
            new FiringQuery(coneMin, coneMax, atmosphere), strict, variants,
            Normalise(request.Filters?.Surface), Normalise(request.Filters?.Opacity), Normalise(request.Filters?.ColourFamily),
            clayBodies, topK, request.IncludeRecipeMetadata == true, chemistryWarnings.Distinct().ToList(), oxideWeightPercentJson, colourants,
            Trim(request.Query?.Name, 200), typed, colorantMaterials, colorantOxides);
    }

    /// <summary>
    /// Recipes are system scoped: every active recipe can be searched by, and shown on results to, any signed-in
    /// account, whoever created it. Kept as one method so a future rule has a single place to live.
    /// </summary>
    public Task<bool> CanUseRecipeAsync(Recipe recipe, SearchRequester requester, CancellationToken cancellationToken) => Task.FromResult(recipe.IsActive);

    /// <summary>A snapshot calculated in memory from the recipe's rows, or null when they cannot be calculated.</summary>
    private RecipeCalculationResult? TransientSnapshot(Recipe recipe)
    {
        if (!chemistry.TryGet(out var data, out _)) return null;
        RecipeResult? result;
        try
        {
            var rows = UmfCalculationService.WithAliases(UmfCalculationService.RowsFromRecipe(recipe), data, chemistry.Curation);
            if (UmfCalculationService.Resolve(data, rows, chemistry.Curation).Any(r => r.Status != "resolved")) return null;
            result = GlazeChemistry.CalculateRecipe(rows, data);
        }
        catch (ApiProblemException) { return null; } // no material rows, or mixed units
        if (result is null || result.Chem.FluxSum <= GlazeChemistry.Zero) return null;
        return new RecipeCalculationResult
        {
            Id = Guid.Empty, RecipeId = recipe.Id, CalculationProfile = GlazeChemistry.CalculationProfile, CalculationVersion = GlazeChemistry.CalculationVersion,
            ChemistrySchemaVersion = ChemistrySchema.Version, UmfJson = JsonSerializer.Serialize(result.Chem.Umf),
            InputJson = JsonSerializer.Serialize(new { lines = result.Lines }, Json)
        };
    }

    // ---- Filters (plan §11.6) ----

    private static VectorFilter BuildVectorFilter(CanonicalQuery query, decimal coneTolerance)
    {
        var parts = new List<VectorFilter>
        {
            VectorFilter.Eq(VectorMetadataKeys.ApprovalStatus, "approved"),
            VectorFilter.Eq(VectorMetadataKeys.Indexable, true)
        };
        if (query.TileVariants.Count > 0) parts.Add(VectorFilter.AnyOf(VectorMetadataKeys.TileVariant, query.TileVariants));
        if (query.ClayBodies.Count > 0) parts.Add(VectorFilter.AnyOf(VectorMetadataKeys.ClayBody, query.ClayBodies));
        if (query.Strict)
        {
            // The tile's range overlaps the searched cones ±1: image.coneMin <= high AND image.coneMax >= low. Unrecorded cones fail strict filters.
            if (query.Firing.ConeMin is { } min && query.Firing.ConeMax is { } max)
            {
                var (low, high) = HybridRanker.ConeWindow(min, max, coneTolerance);
                parts.Add(VectorFilter.Lte(VectorMetadataKeys.ConeMin, (double)high));
                parts.Add(VectorFilter.Gte(VectorMetadataKeys.ConeMax, (double)low));
            }
            if (query.Firing.Atmosphere is { } atm) parts.Add(VectorFilter.Eq(VectorMetadataKeys.Atmosphere, atm));
        }
        return VectorFilter.All(parts.ToArray());
    }

    /// <summary>The same hard filters re-applied to authoritative PostgreSQL values, so stale vector metadata cannot leak a result.</summary>
    private static bool PassesHardFilters(ImageAsset asset, CanonicalQuery query, decimal coneTolerance)
    {
        if (query.TileVariants.Count > 0 && !query.TileVariants.Contains(asset.TileVariantKey, StringComparer.OrdinalIgnoreCase)) return false;
        if (query.ClayBodies.Count > 0 && (asset.ClayBody is null || !query.ClayBodies.Contains(asset.ClayBody, StringComparer.Ordinal))) return false;
        if (!query.Strict) return true;
        if (query.Firing.ConeMin is { } min && query.Firing.ConeMax is { } max)
        {
            var cMin = asset.ConeMin ?? asset.ConeMax;
            var cMax = asset.ConeMax ?? asset.ConeMin;
            var (low, high) = HybridRanker.ConeWindow(min, max, coneTolerance);
            if (cMin is null || cMax is null || cMin > high || cMax < low) return false;
        }
        if (query.Firing.Atmosphere is { } atm && !string.Equals(asset.Atmosphere, atm, StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    // ---- Ranking (plan §11.5) ----

    /// <summary>GroupKey is the image's primary recipe (or the image itself), used only to pick one suggestion per recipe.</summary>
    private sealed record Scored(SearchResultItem Item, bool VerifiedRecipe, bool QualityPassed, Guid GroupKey);

    private Scored Score(ImageAsset asset, CanonicalQuery query, double? textSimilarity, SearchRequester requester, ChemistryData? data, MaterialCuration curation)
    {
        var labels = ParseLabels(asset.LabelsJson);
        var limitations = new List<string>();
        var matched = new List<string>();
        var reductions = 0;

        // Chemistry: authoritative UMF from the pinned snapshot, compared locally (cosine + bounded weighted distance).
        ChemistryComparison? chemistry = null;
        IReadOnlyDictionary<string, decimal>? candidateUmf = null;
        if (asset.ChemistryStatus == ChemistryStatus.Linked && asset.CalculationResult is { } snapshot)
        {
            using var document = JsonDocument.Parse(snapshot.UmfJson);
            var validation = ChemistrySchema.ValidateUmf(document.RootElement);
            if (validation.IsValid) candidateUmf = validation.Umf;
            if (candidateUmf is not null)
            {
                chemistry = HybridRanker.CompareChemistry(query.Umf, candidateUmf, _options.ChemistryCosineShare);
                if (snapshot.CalculationProfile == ChemistrySchema.ProvidedUmfProfile)
                    limitations.Add("Tile chemistry is a supplied UMF, not calculated from verified materials.");
                else if (query.CalculationProfile is { } qp && !string.Equals(qp, snapshot.CalculationProfile, StringComparison.Ordinal))
                    limitations.Add($"Chemistry compared across calculation profiles {qp} and {snapshot.CalculationProfile}.");
            }
        }
        if (chemistry is null) { reductions++; limitations.Add("No chemistry is recorded for this tile; it matched on firing and labels only."); }
        else
        {
            if (chemistry.Coverage < _options.MinimumChemistryCoverage)
            {
                reductions++;
                limitations.Add($"Tile chemistry does not report {string.Join(", ", chemistry.MissingQueryOxides)} from the query; unmeasured is not treated as zero.");
            }
            if (chemistry.FluxCosine is >= 0.95) matched.Add("fluxBalance");
            if (chemistry.SilicaDelta is { } ds && ds <= 0.25m) matched.Add("silica");
            if (chemistry.AluminaDelta is { } da && da <= 0.05m) matched.Add("alumina");
            if (chemistry.BoronDelta is { } db && db <= 0.05m) matched.Add("boron");
            if (chemistry.SilicaAluminaRatioDelta is <= 0.10) matched.Add("silicaAluminaRatio");
        }

        var firing = HybridRanker.Firing(query.Firing, asset.ConeMin, asset.ConeMax, asset.Atmosphere, _options.ConeTolerance);
        if (firing.Cone is ConeMatch.Exact or ConeMatch.Compatible) matched.Add("cone");
        if (firing.Atmosphere == AtmosphereMatch.Exact) matched.Add("atmosphere");
        if (asset.ConeMin is null && asset.ConeMax is null) limitations.Add("Firing cone was not recorded for this tile.");
        if (string.IsNullOrWhiteSpace(asset.Atmosphere)) limitations.Add("Firing atmosphere was not recorded for this tile.");
        limitations.Add("Exact firing schedule was not recorded.");
        if (query.TileVariants.Count > 0) matched.Add("tileVariant");
        if (query.ClayBodies.Count > 0) matched.Add("clayBody");
        if (asset.ClayBody is null) limitations.Add("Clay body was not recorded for this tile.");

        // Label/text: text-embedding cosine, blended with the share of requested soft labels the tile carries.
        double? labelMatch = null;
        var requested = new List<(string Key, IReadOnlyList<string> Values)>();
        if (query.Surface.Count > 0) requested.Add(("surface", query.Surface));
        if (query.Opacity.Count > 0) requested.Add(("opacity", query.Opacity));
        if (query.ColourFamily.Count > 0) requested.Add(("colourFamily", query.ColourFamily));
        if (requested.Count > 0)
        {
            var hits = 0;
            foreach (var (key, values) in requested)
                if (labels.TryGetValue(key, out var v) && values.Contains(v.ToLowerInvariant(), StringComparer.Ordinal)) { hits++; matched.Add(key); }
            labelMatch = (double)hits / requested.Count;
        }
        double? labelOrText = (textSimilarity, labelMatch) switch
        {
            ({ } t, { } l) => 0.5 * t + 0.5 * l,
            ({ } t, null) => t,
            (null, { } l) => l,
            _ => null
        };
        if (textSimilarity is null) reductions++;

        // Image similarity only exists for image queries, which this release does not accept; it is renormalised away
        // without a confidence penalty because the query never offered that modality.
        double? imageSimilarity = null;

        // Colorants, both kinds: metallic oxides from the two UMFs, and stains (which the UMF cannot see) by material
        // and amount when both sides know them. Left out only when the tile has no chemistry at all.
        double? colourant = null;
        var tileColourants = data is null ? null : Colourants.ForAsset(asset, data, curation);
        if (candidateUmf is not null)
        {
            var searched = query.ColorantProfile;
            var tile = new ColorantProfile(ColorantMatch.OxidesOf(asset.CalculationResult!.UmfJson), tileColourants);
            var withStains = ColorantMatch.ComparesStains(searched, tile);
            colourant = ColorantMatch.Similarity(searched, tile);
            if (ColorantMatch.HasAny(searched) && colourant >= 0.8) matched.Add("colourants");
            if (colourant < 0.8) limitations.Add($"Colorants differ: you searched {ColorantMatch.Describe(searched, withStains)}; this tile has {ColorantMatch.Describe(tile, withStains)}.");
            // A clearly different colour is not a close match, however close the base glaze is.
            if (colourant < 0.5) reductions++;
        }
        if (query.Colourants is { Count: > 0 } && tileColourants is null)
            limitations.Add("This tile's materials are not recorded, so stains were not compared.");
        else if (query.Colourants is null && tileColourants is { Count: > 0 })
            limitations.Add($"This tile adds {Colourants.Describe(tileColourants)}, a stain, which does not show in a UMF, so it was not compared.");

        var score = HybridRanker.Combine(_options.Weights, chemistry?.Similarity, firing.Score, labelOrText, imageSimilarity, colourant);
        var band = HybridRanker.Band(score, reductions, _options);

        var primary = asset.Recipes.OrderByDescending(x => x.Verified).ThenBy(x => x.CreatedAt).FirstOrDefault();
        if (primary is { Verified: false }) limitations.Add("The recipe link has not been verified by a curator.");
        if (asset.QualityStatus == QualityStatus.NeedsReview) limitations.Add("Approved with image-quality notes: " + string.Join(", ", ParseList(asset.QualityReasonsJson)) + ".");

        // Recipes are system scoped: shown to any signed-in account that asks for them.
        var mayShowRecipe = primary?.Recipe is not null && query.IncludeRecipeMetadata;
        var source = mayShowRecipe
            ? new SearchSourceSummary("approved_image", primary!.RecipeId, primary.Recipe!.Code, "recipe", NameOrNull(primary.Recipe.Name))
            : new SearchSourceSummary("approved_image", null, null, "image_only");

        var item = new SearchResultItem(0, ResultTypeRealTile, asset.Id, null, null, Math.Round(score, 4), band,
            new SearchScoreBreakdown(R(chemistry?.Similarity), R(firing.Score), R(labelOrText), imageSimilarity, R(chemistry?.Cosine), R(chemistry?.Distance), R(chemistry?.Coverage), R(textSimilarity), R(labelMatch), R(colourant)),
            matched.Distinct().ToList(),
            new SearchTileSummary(asset.TileVariantKey, labels.GetValueOrDefault("surface"), labels.GetValueOrDefault("opacity"), labels.GetValueOrDefault("colourFamily"), asset.ClayBody),
            new SearchFiringSummary(asset.ConeMin, asset.ConeMax, asset.Atmosphere),
            source, limitations);
        return new Scored(item, primary?.Verified == true, asset.QualityStatus == QualityStatus.Passed, primary?.RecipeId ?? asset.Id);
    }

    // ---- Persistence and URLs ----

    private async Task<RecipeImageSearchResponse> RehydrateAsync(SearchRun run, SearchRequester requester, CancellationToken cancellationToken)
    {
        var items = JsonSerializer.Deserialize<List<SearchResultItem>>(run.ResultsJson, Json) ?? [];
        var warnings = JsonSerializer.Deserialize<List<string>>(run.WarningsJson, Json) ?? [];
        var ids = items.Select(x => x.ImageId).ToList();
        var stillApproved = ids.Count == 0 ? [] : await db.ImageAssets.AsNoTracking()
            .Where(x => ids.Contains(x.Id) && x.ApprovalStatus == ApprovalStatus.Approved && x.QualityStatus != QualityStatus.Failed)
            .Select(x => x.Id).ToHashSetAsync(cancellationToken);
        var visible = items.Where(x => stillApproved.Contains(x.ImageId)).ToList();
        if (visible.Count < items.Count) warnings.Add("results_changed_since_search");
        var signed = await SignAsync(visible, cancellationToken);
        var similarStored = SimilarFromSnapshot(run.QuerySnapshotJson);
        var similarIds = similarStored.Select(x => x.ImageId).ToList();
        var similarApproved = similarIds.Count == 0 ? [] : await db.ImageAssets.AsNoTracking()
            .Where(x => similarIds.Contains(x.Id) && x.ApprovalStatus == ApprovalStatus.Approved && x.QualityStatus != QualityStatus.Failed)
            .Select(x => x.Id).ToHashSetAsync(cancellationToken);
        var signedSimilar = await SignAsync(similarStored.Where(x => similarApproved.Contains(x.ImageId)).ToList(), cancellationToken);
        using var snapshot = JsonDocument.Parse(run.QuerySnapshotJson);
        var q = snapshot.RootElement.GetProperty("query");
        string? Str(string name) => q.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
        IReadOnlyDictionary<string, decimal>? umf = q.TryGetProperty("umf", out var u) && u.ValueKind == JsonValueKind.Object ? u.Deserialize<Dictionary<string, decimal>>(Json) : null;
        IReadOnlyList<Colourant>? colourants = q.TryGetProperty("colourants", out var c) && c.ValueKind == JsonValueKind.Array ? c.Deserialize<List<Colourant>>(Json) : null;
        IReadOnlyList<ColorantMaterial>? colorantMaterials = q.TryGetProperty("colorantMaterials", out var cm) && cm.ValueKind == JsonValueKind.Array ? cm.Deserialize<List<ColorantMaterial>>(Json) : null;
        return new RecipeImageSearchResponse(run.Id, new SearchQuerySummary(run.QueryType, "sha256:" + run.InputHash, Str("calculationProfile"), Str("recipeCode"), umf, colourants, colorantMaterials),
            new SearchRankerSummary(run.RankerVersion, run.CandidateCount, signed.Count, _options.MinimumConfidence), signed, warnings, run.CreatedAt,
            signed.FirstOrDefault(), signedSimilar);
    }

    private static List<SearchResultItem> SimilarFromSnapshot(string querySnapshotJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(querySnapshotJson);
            return doc.RootElement.TryGetProperty("similar", out var s) && s.ValueKind == JsonValueKind.Array ? s.Deserialize<List<SearchResultItem>>(Json) ?? [] : [];
        }
        catch (JsonException) { return []; }
    }

    private async Task<List<SearchResultItem>> SignAsync(IReadOnlyList<SearchResultItem> items, CancellationToken cancellationToken)
    {
        if (items.Count == 0) return [];
        var ids = items.Select(x => x.ImageId).ToList();
        var assets = await db.ImageAssets.AsNoTracking().Include(x => x.Recipes).ThenInclude(x => x.Recipe).ThenInclude(x => x!.BaseGlaze)
            .Where(x => ids.Contains(x.Id)).ToDictionaryAsync(x => x.Id, cancellationToken);
        var result = new List<SearchResultItem>(items.Count);
        foreach (var item in items)
        {
            if (!assets.TryGetValue(item.ImageId, out var asset)) continue;
            // Lab code as filename (same code everywhere). Derivatives are used when they exist; until the derivatives
            // slice ships, both URLs point at the validated original.
            var code = asset.Recipes.OrderByDescending(x => x.Verified).ThenBy(x => x.CreatedAt).Select(x => x.Recipe?.Code).FirstOrDefault(x => x is not null);
            var (display, thumbnail) = await TilePhotoUrls.SignAsync(storage, asset, code, cancellationToken);
            // The base code (and, for runs saved before results carried it, the recipe name) comes from the recipe as it
            // is now, so the app can open the recipe section on the base and all its variations.
            var source = item.Source;
            if (source is { MetadataVisibility: "recipe", RecipeId: { } recipeId } && asset.Recipes.FirstOrDefault(x => x.RecipeId == recipeId)?.Recipe is { } recipe)
                source = source with { RecipeName = source.RecipeName ?? NameOrNull(recipe.Name), BaseCode = recipe.BaseGlaze?.Code };
            result.Add(item with { DisplayUrl = display, ThumbnailUrl = thumbnail, Source = source });
        }
        return result;
    }

    // ---- Helpers ----

    private static string? NameOrNull(string? name) => string.IsNullOrWhiteSpace(name) ? null : name.Trim();

    private async Task<IReadOnlyList<VectorMatch>?> QuerySafeAsync(VectorQuery query, CancellationToken cancellationToken)
    {
        try { return await vectorIndex.QueryAsync(query, cancellationToken); }
        catch (VectorIndexException ex)
        {
            logger.LogWarning(ex, "Vector query on {Namespace} failed ({Code})", query.Namespace, ex.Code);
            return null;
        }
    }

    private static string HashQuery(CanonicalQuery q)
    {
        var canonical = JsonSerializer.Serialize(new
        {
            q.Type,
            umf = ChemistrySchema.CanonicalJson(q.Umf),
            q.CalculationProfile,
            q.RecipeId,
            q.CalculationResultId,
            firing = new { q.Firing.ConeMin, q.Firing.ConeMax, q.Firing.Atmosphere, q.Strict },
            // Clay bodies join the hash only when filtered on, so hashes of earlier queries stay the same.
            filters = q.ClayBodies.Count == 0 ? (object)new { q.TileVariants, q.Surface, q.Opacity, q.ColourFamily } : new { q.TileVariants, q.Surface, q.Opacity, q.ColourFamily, q.ClayBodies },
            q.TopK,
            q.IncludeRecipeMetadata
        });
        // Stains join the hash only when known, so UMF-query hashes stay as before and two recipes that differ only
        // in stain no longer share one.
        if (q.Colourants is { } colourants)
            canonical += "|colourants:" + string.Join(";", colourants.Select(c => $"{c.MaterialId}={c.PercentOfBase.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static List<string> Normalise(IReadOnlyList<string>? values) =>
        values is null ? [] : values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim().ToLowerInvariant()).Distinct(StringComparer.Ordinal).OrderBy(v => v, StringComparer.Ordinal).ToList();

    private static string? Trim(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        return trimmed.Length > max ? trimmed[..max] : trimmed;
    }
    private static double? R(double? value) => value is null ? null : Math.Round(value.Value, 4);

    private static Dictionary<string, string> ParseLabels(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            return document.RootElement.ValueKind != JsonValueKind.Object ? [] : document.RootElement.EnumerateObject()
                .Where(p => p.Value.ValueKind == JsonValueKind.String).ToDictionary(p => p.Name, p => p.Value.GetString()!, StringComparer.Ordinal);
        }
        catch (JsonException) { return []; }
    }

    private static List<string> ParseList(string json)
    {
        try { return JsonSerializer.Deserialize<List<string>>(json) ?? []; } catch (JsonException) { return []; }
    }

    private static ApiProblemException Validation(string field, string message) => ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });

    private static ApiProblemException ValidationProblem(Dictionary<string, string[]> errors) =>
        new(400, "validation_error", "Validation failed", errors.Values.SelectMany(x => x).FirstOrDefault() ?? "One or more fields are invalid.", new Dictionary<string, object?> { ["errors"] = errors });
}
