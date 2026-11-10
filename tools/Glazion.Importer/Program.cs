// Glazion bulk importer.
//
// Streams a labelled corpus into Cloudflare R2 through the Glazion API without ever holding R2 credentials:
//   1. create (or resume) an import batch
//   2. for each chunk of manifest rows: hash files locally, register rows, receive API-generated keys + presigned PUTs
//   3. PUT files directly to R2 with bounded concurrency
//   4. ask the API to verify the objects (HeadObject) and create image assets + validation jobs
// Resumable: server-side registration is idempotent on externalRecordId and a local state file skips completed rows.
//
// Usage:
//   dotnet run --project tools/Glazion.Importer -- \
//     --api https://api.example.com --token <admin-session-token> \
//     --manifest ./corpus/manifest.jsonl --files ./corpus/images \
//     [--batch-name "corpus-2026-09"] [--batch-id <existing>] [--concurrency 16] [--chunk 200] [--auto-approve] [--state ./import-state.json]

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

var args_ = Args.Parse(args);
var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
json.Converters.Add(new JsonStringEnumConverter());

using var api = new HttpClient { BaseAddress = new Uri(args_.Api), Timeout = TimeSpan.FromMinutes(5) };
api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", args_.Token);
using var r2 = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };

var state = ImportState.Load(args_.StatePath);

// 1. batch
Guid batchId;
if (args_.BatchId is { } existingId) batchId = existingId;
else if (state.BatchId is { } savedId) { batchId = savedId; Console.WriteLine($"Resuming batch {batchId}"); }
else
{
    var created = await api.PostAsJsonAsync("api/v2/admin/imports", new { name = args_.BatchName, sourceLabel = args_.SourceLabel, defaultTileVariantKey = args_.DefaultTileVariant, autoApprove = args_.AutoApprove }, json);
    created.EnsureSuccessStatusCode();
    batchId = (await created.Content.ReadFromJsonAsync<JsonElement>(json)).GetProperty("id").GetGuid();
    Console.WriteLine($"Created batch {batchId}");
}
state.BatchId = batchId;
state.Save(args_.StatePath);

// 2. manifest
var rows = new List<ManifestRow>();
await foreach (var line in File.ReadLinesAsync(args_.ManifestPath))
{
    if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#')) continue;
    var row = JsonSerializer.Deserialize<ManifestRow>(line, json) ?? throw new InvalidOperationException("Invalid manifest line.");
    rows.Add(row);
}
var pendingRows = rows.Where(r => !state.Completed.Contains(r.ExternalRecordId)).ToList();
Console.WriteLine($"Manifest: {rows.Count} rows, {pendingRows.Count} pending, {state.Completed.Count} already completed");

var totals = new Totals();
var stopwatch = Stopwatch.StartNew();
var gate = new SemaphoreSlim(args_.Concurrency);

foreach (var chunk in pendingRows.Chunk(args_.Chunk))
{
    // 2a. hash + size locally (streamed, bounded parallelism)
    var prepared = new ConcurrentDictionary<string, (ManifestRow Row, string Path, long Size, string Sha256)>();
    await Parallel.ForEachAsync(chunk, new ParallelOptions { MaxDegreeOfParallelism = args_.Concurrency }, async (row, ct) =>
    {
        var path = Path.Combine(args_.FilesDir, row.FileName);
        if (!File.Exists(path)) { totals.MissingFile(row.ExternalRecordId); return; }
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct)).ToLowerInvariant();
        prepared[row.ExternalRecordId] = (row, path, fs.Length, hash);
    });
    if (prepared.IsEmpty) continue;

    // 2b. register
    var registerBody = new
    {
        records = prepared.Values.Select(p => new
        {
            externalRecordId = p.Row.ExternalRecordId,
            fileName = p.Row.FileName,
            contentType = p.Row.ContentType ?? ContentTypeFor(p.Row.FileName),
            sizeBytes = p.Size,
            sha256 = p.Sha256,
            tileVariantKey = p.Row.TileVariantKey,
            coneMin = p.Row.ConeMin,
            coneMax = p.Row.ConeMax,
            atmosphere = p.Row.Atmosphere,
            labels = p.Row.Labels,
            recipeId = p.Row.RecipeId,
            umf = p.Row.Umf
        }).ToList()
    };
    var registerResponse = await api.PostAsJsonAsync($"api/v2/admin/imports/{batchId}/records", registerBody, json);
    if (!registerResponse.IsSuccessStatusCode) throw new InvalidOperationException($"Register failed: {(int)registerResponse.StatusCode} {await registerResponse.Content.ReadAsStringAsync()}");
    var registered = await registerResponse.Content.ReadFromJsonAsync<RegisterResponse>(json) ?? throw new InvalidOperationException("Empty register response.");

    // 2c. upload (direct to R2, presigned, bounded)
    var toComplete = new ConcurrentBag<string>();
    var uploadTasks = registered.Records.Select(async reg =>
    {
        if (!reg.Accepted) { totals.Rejected(reg.ExternalRecordId, string.Join(",", reg.Errors)); return; }
        if (reg.Status is not null && reg.Status != "Registered") { state.MarkCompleted(reg.ExternalRecordId); totals.AlreadyDone(); return; }
        if (reg.Upload is null) { totals.Rejected(reg.ExternalRecordId, "no_presigned_url"); return; }
        var p = prepared[reg.ExternalRecordId];
        await gate.WaitAsync();
        try
        {
            await PutWithRetryAsync(r2, reg.Upload, p.Path, p.Size);
            toComplete.Add(reg.ExternalRecordId);
            totals.Uploaded(p.Size);
        }
        catch (Exception ex) { totals.UploadFailed(reg.ExternalRecordId, ex.Message); }
        finally { gate.Release(); }
    });
    await Task.WhenAll(uploadTasks);

    // 2d. complete (server verifies HeadObject, creates assets + jobs)
    if (!toComplete.IsEmpty)
    {
        var completeResponse = await api.PostAsJsonAsync($"api/v2/admin/imports/{batchId}/records/complete", new { externalRecordIds = toComplete.ToArray() }, json);
        if (!completeResponse.IsSuccessStatusCode) throw new InvalidOperationException($"Complete failed: {(int)completeResponse.StatusCode} {await completeResponse.Content.ReadAsStringAsync()}");
        var completed = await completeResponse.Content.ReadFromJsonAsync<CompleteResponse>(json) ?? throw new InvalidOperationException("Empty complete response.");
        foreach (var c in completed.Records)
        {
            if (c.Error is null && c.ImageId is not null) { state.MarkCompleted(c.ExternalRecordId); totals.Completed(); }
            else totals.CompleteFailed(c.ExternalRecordId, c.Error ?? "unknown");
        }
    }
    state.Save(args_.StatePath);
    Console.WriteLine($"[{stopwatch.Elapsed:hh\\:mm\\:ss}] {totals}");
}

Console.WriteLine();
Console.WriteLine($"Finished batch {batchId} in {stopwatch.Elapsed:hh\\:mm\\:ss}. {totals}");
if (totals.Failures.Count > 0)
{
    Console.WriteLine("Failures (externalRecordId: reason):");
    foreach (var (id, reason) in totals.Failures.OrderBy(x => x.Key)) Console.WriteLine($"  {id}: {reason}");
}
var report = await api.GetFromJsonAsync<JsonElement>($"api/v2/admin/imports/{batchId}", json);
Console.WriteLine("Server report: " + report.GetProperty("counts").GetRawText());
return totals.Failures.Count == 0 ? 0 : 2;

static async Task PutWithRetryAsync(HttpClient r2, Presigned presigned, string path, long size)
{
    var delay = TimeSpan.FromSeconds(2);
    for (var attempt = 1; ; attempt++)
    {
        try
        {
            await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
            using var content = new StreamContent(fs);
            content.Headers.ContentLength = size;
            using var request = new HttpRequestMessage(HttpMethod.Put, presigned.Url) { Content = content };
            foreach (var (name, value) in presigned.Headers)
            {
                if (name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) content.Headers.ContentType = MediaTypeHeaderValue.Parse(value);
                else request.Headers.TryAddWithoutValidation(name, value);
            }
            using var response = await r2.SendAsync(request);
            if (response.IsSuccessStatusCode) return;
            if ((int)response.StatusCode is >= 400 and < 500 && response.StatusCode != System.Net.HttpStatusCode.RequestTimeout)
                throw new InvalidOperationException($"R2 rejected PUT with {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
            if (attempt >= 5) throw new InvalidOperationException($"R2 PUT failed after {attempt} attempts with {(int)response.StatusCode}.");
        }
        catch (HttpRequestException) when (attempt < 5) { }
        catch (TaskCanceledException) when (attempt < 5) { }
        await Task.Delay(delay + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 500)));
        delay *= 2;
    }
}

static string ContentTypeFor(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
{
    ".jpg" or ".jpeg" => "image/jpeg",
    ".png" => "image/png",
    ".webp" => "image/webp",
    _ => "application/octet-stream"
};

sealed record ManifestRow(string ExternalRecordId, string FileName, string? ContentType, string? TileVariantKey, decimal? ConeMin, decimal? ConeMax, string? Atmosphere, Dictionary<string, string>? Labels, Guid? RecipeId, JsonElement? Umf);
sealed record Presigned(string Url, string ObjectKey, DateTimeOffset ExpiresAt, Dictionary<string, string> Headers);
sealed record Registration(string ExternalRecordId, bool Accepted, string? Status, string? ObjectKey, Presigned? Upload, List<string> Errors, List<string> Warnings);
sealed record RegisterResponse(Guid BatchId, List<Registration> Records);
sealed record Completion(string ExternalRecordId, string Status, Guid? ImageId, string? Error);
sealed record CompleteResponse(Guid BatchId, List<Completion> Records);

sealed class ImportState
{
    public Guid? BatchId { get; set; }
    public HashSet<string> Completed { get; set; } = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();

    public void MarkCompleted(string id) { lock (_lock) Completed.Add(id); }

    public static ImportState Load(string path) => File.Exists(path) ? JsonSerializer.Deserialize<ImportState>(File.ReadAllText(path)) ?? new() : new();

    public void Save(string path)
    {
        lock (_lock)
        {
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this));
            File.Move(tmp, path, overwrite: true);
        }
    }
}

sealed class Totals
{
    private long _uploadedBytes;
    private int _uploaded, _completed, _alreadyDone;
    public ConcurrentDictionary<string, string> Failures { get; } = new();
    public void Uploaded(long bytes) { Interlocked.Increment(ref _uploaded); Interlocked.Add(ref _uploadedBytes, bytes); }
    public void Completed() => Interlocked.Increment(ref _completed);
    public void AlreadyDone() => Interlocked.Increment(ref _alreadyDone);
    public void MissingFile(string id) => Failures[id] = "missing_file";
    public void Rejected(string id, string reason) => Failures[id] = "rejected:" + reason;
    public void UploadFailed(string id, string reason) => Failures[id] = "upload_failed:" + reason;
    public void CompleteFailed(string id, string reason) => Failures[id] = "complete_failed:" + reason;
    public override string ToString() => $"uploaded={_uploaded} ({_uploadedBytes / 1048576.0:F1} MiB) completed={_completed} skipped={_alreadyDone} failed={Failures.Count}";
}

sealed record Args(string Api, string Token, string ManifestPath, string FilesDir, string BatchName, Guid? BatchId, string SourceLabel, string DefaultTileVariant, bool AutoApprove, int Concurrency, int Chunk, string StatePath)
{
    public static Args Parse(string[] argv)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var flags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < argv.Length; i++)
        {
            if (!argv[i].StartsWith("--")) continue;
            var key = argv[i][2..];
            if (i + 1 < argv.Length && !argv[i + 1].StartsWith("--")) map[key] = argv[++i];
            else flags.Add(key);
        }
        string Required(string key) => map.TryGetValue(key, out var v) ? v : throw new ArgumentException($"--{key} is required");
        var token = map.TryGetValue("token", out var t) ? t : Environment.GetEnvironmentVariable("GLAZION_TOKEN") ?? throw new ArgumentException("--token or GLAZION_TOKEN is required");
        return new Args(
            Required("api").TrimEnd('/') + "/",
            token,
            Required("manifest"),
            Required("files"),
            map.GetValueOrDefault("batch-name", $"import-{DateTime.UtcNow:yyyyMMdd-HHmm}"),
            map.TryGetValue("batch-id", out var b) ? Guid.Parse(b) : null,
            map.GetValueOrDefault("source", "curated_dataset"),
            map.GetValueOrDefault("tile-variant", "white"),
            flags.Contains("auto-approve"),
            int.Parse(map.GetValueOrDefault("concurrency", "16")),
            Math.Clamp(int.Parse(map.GetValueOrDefault("chunk", "200")), 1, 200),
            map.GetValueOrDefault("state", "import-state.json"));
    }
}
