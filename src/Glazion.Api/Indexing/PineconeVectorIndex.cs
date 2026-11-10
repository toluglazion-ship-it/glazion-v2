using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Glazion.Api.Infrastructure;
using Microsoft.Extensions.Options;

namespace Glazion.Api.Indexing;

public sealed class VectorIndexOptions
{
    /// <summary>"Pinecone" or "InMemory".</summary>
    public string Provider { get; set; } = "InMemory";
    public PineconeOptions Pinecone { get; set; } = new();
}

public sealed class PineconeOptions
{
    public string ApiKey { get; set; } = string.Empty;
    /// <summary>Default index data-plane host, e.g. https://glazion-abc123.svc.aped-4627-b74a.pinecone.io</summary>
    public string IndexHost { get; set; } = string.Empty;
    /// <summary>
    /// Optional namespace-prefix → index host overrides. Pinecone fixes one dimension per index, so chemistry (22) and
    /// text (model-sized) vectors need separate indexes: e.g. { "images-text-": "https://glazion-text-....pinecone.io" }.
    /// Longest matching prefix wins; namespaces without a match use IndexHost.
    /// </summary>
    public Dictionary<string, string> NamespaceIndexHosts { get; set; } = new(StringComparer.Ordinal);
    /// <summary>Host of the index for image chemistry vectors (dimension 22, cosine). Wins over the maps above for "images-chemistry-*".</summary>
    public string ChemistryIndexHost { get; set; } = string.Empty;
    /// <summary>Host of the index for image text vectors (dimension = Embeddings dimension, cosine). Wins for "images-text-*".</summary>
    public string TextIndexHost { get; set; } = string.Empty;
    public string ApiVersion { get; set; } = "2025-01";
    public int UpsertBatchSize { get; set; } = 100;
    public int MaxRetries { get; set; } = 4;
    public int RetryBaseMilliseconds { get; set; } = 500;
}

/// <summary>
/// IVectorIndex over the Pinecone data-plane REST API. Keeps provider details (payload shapes, filter syntax, rate
/// limits, error bodies) out of application code: batches upserts, retries transient failures with backoff, honours
/// Retry-After, and maps HTTP outcomes to VectorIndexErrorCode. Credentials come only from configuration.
/// </summary>
public sealed class PineconeVectorIndex : IVectorIndex
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    private readonly HttpClient _http;
    private readonly PineconeOptions _options;
    private readonly ILogger<PineconeVectorIndex> _logger;

    public PineconeVectorIndex(HttpClient http, IOptions<VectorIndexOptions> options, ILogger<PineconeVectorIndex> logger)
    {
        _http = http;
        _options = options.Value.Pinecone;
        _logger = logger;
        var hasHosts = !string.IsNullOrWhiteSpace(_options.IndexHost) || (!string.IsNullOrWhiteSpace(_options.ChemistryIndexHost) && !string.IsNullOrWhiteSpace(_options.TextIndexHost));
        if (!hasHosts || string.IsNullOrWhiteSpace(_options.ApiKey))
            throw new InvalidOperationException("Pinecone is not configured. Set VectorIndex__Pinecone__ApiKey and VectorIndex__Pinecone__ChemistryIndexHost + TextIndexHost (or IndexHost).");
        _http.DefaultRequestHeaders.Remove("Api-Key");
        _http.DefaultRequestHeaders.Add("Api-Key", _options.ApiKey);
        _http.DefaultRequestHeaders.Remove("X-Pinecone-API-Version");
        _http.DefaultRequestHeaders.Add("X-Pinecone-API-Version", _options.ApiVersion);
    }

    public string ProviderName => "Pinecone";

    public async Task UpsertAsync(string @namespace, IReadOnlyList<VectorRecord> records, CancellationToken cancellationToken)
    {
        if (records.Count == 0) return;
        foreach (var batch in records.Chunk(Math.Max(1, _options.UpsertBatchSize)))
        {
            var body = new { vectors = batch.Select(r => new PineconeVector(r.Id, r.Values, r.Metadata)).ToArray(), @namespace };
            var response = await SendAsync<UpsertResponse>(HttpMethod.Post, HostFor(@namespace), "vectors/upsert", body, cancellationToken);
            if (response is not null && response.UpsertedCount != batch.Length)
                _logger.LogWarning("Pinecone upsert to {Namespace} reported {Reported} of {Sent} vectors", @namespace, response.UpsertedCount, batch.Length);
        }
    }

    public async Task DeleteAsync(string @namespace, IReadOnlyList<string> ids, CancellationToken cancellationToken)
    {
        if (ids.Count == 0) return;
        foreach (var batch in ids.Chunk(1000))
            await SendAsync<JsonElement?>(HttpMethod.Post, HostFor(@namespace), "vectors/delete", new { ids = batch, @namespace }, cancellationToken);
    }

    public async Task<IReadOnlyList<VectorMatch>> QueryAsync(VectorQuery query, CancellationToken cancellationToken)
    {
        if (query.TopK <= 0 || query.TopK > 10_000) throw new VectorIndexException(VectorIndexErrorCode.InvalidRequest, "topK must be between 1 and 10000.");
        var body = new
        {
            @namespace = query.Namespace,
            topK = query.TopK,
            vector = query.Vector,
            includeMetadata = query.IncludeMetadata,
            includeValues = false,
            filter = query.Filter is null ? null : TranslateFilter(query.Filter)
        };
        var response = await SendAsync<QueryResponse>(HttpMethod.Post, HostFor(query.Namespace), "query", body, cancellationToken);
        return response?.Matches?.Select(m => new VectorMatch(m.Id, m.Score, ToMetadata(m.Metadata))).ToList() ?? [];
    }

    public async Task<IReadOnlyDictionary<string, VectorRecord>> FetchAsync(string @namespace, IReadOnlyList<string> ids, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, VectorRecord>(StringComparer.Ordinal);
        if (ids.Count == 0) return result;
        // Fetch is a GET with ids in the query string; chunk so the URL stays well under proxy limits.
        foreach (var chunk in ids.Chunk(100))
        {
            var path = "vectors/fetch?namespace=" + Uri.EscapeDataString(@namespace) + string.Concat(chunk.Select(id => "&ids=" + Uri.EscapeDataString(id)));
            var response = await SendAsync<FetchResponse>(HttpMethod.Get, HostFor(@namespace), path, null, cancellationToken);
            if (response?.Vectors is null) continue;
            foreach (var (id, vector) in response.Vectors) result[id] = new VectorRecord(id, vector.Values ?? [], ToMetadata(vector.Metadata));
        }
        return result;
    }

    public async Task<VectorIndexStats> DescribeAsync(CancellationToken cancellationToken)
    {
        var hosts = new[] { _options.IndexHost, _options.ChemistryIndexHost, _options.TextIndexHost }.Concat(_options.NamespaceIndexHosts.Values)
            .Where(h => !string.IsNullOrWhiteSpace(h)).Select(Normalize).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        var dimensions = new HashSet<int>();
        long total = 0;
        foreach (var host in hosts)
        {
            var response = await SendAsync<StatsResponse>(HttpMethod.Post, host, "describe_index_stats", new { }, cancellationToken) ?? new StatsResponse(0, 0, null);
            total += response.TotalVectorCount;
            dimensions.Add(response.Dimension);
            foreach (var (ns, stats) in response.Namespaces ?? []) counts[ns] = stats.VectorCount;
        }
        return new VectorIndexStats(dimensions.Count == 1 ? dimensions.First() : 0, total, counts);
    }

    /// <summary>
    /// Compares each image index's dimension with what this API writes (22 chemistry numbers; the embedding model's
    /// size for text). A mismatch would make every upsert fail, so startup reports it instead of queuing work.
    /// </summary>
    public async Task<IReadOnlyList<string>> CheckDimensionsAsync(int chemistryDimension, int textDimension, CancellationToken cancellationToken)
    {
        var problems = new List<string>();
        foreach (var (label, ns, expected) in new[] { ("chemistry", VectorNamespaces.ImageChemistry(ChemistrySchema.Version), chemistryDimension), ("text", VectorNamespaces.ImageText("check"), textDimension) })
        {
            var host = HostFor(ns);
            var stats = await SendAsync<StatsResponse>(HttpMethod.Post, host, "describe_index_stats", new { }, cancellationToken);
            if (stats is not null && stats.Dimension != expected)
                problems.Add($"The {label} index at {host} has dimension {stats.Dimension}; this API writes {expected}. Point VectorIndex__Pinecone__{(label == "chemistry" ? "ChemistryIndexHost" : "TextIndexHost")} at an index of dimension {expected}.");
        }
        return problems;
    }

    /// <summary>Resolves the index host for a namespace by longest configured prefix. Public for tests.</summary>
    public string HostFor(string @namespace)
    {
        if (@namespace.StartsWith("images-chemistry-", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(_options.ChemistryIndexHost)) return Normalize(_options.ChemistryIndexHost);
        if (@namespace.StartsWith("images-text-", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(_options.TextIndexHost)) return Normalize(_options.TextIndexHost);
        var match = _options.NamespaceIndexHosts.Where(x => @namespace.StartsWith(x.Key, StringComparison.Ordinal)).OrderByDescending(x => x.Key.Length).Select(x => x.Value).FirstOrDefault();
        return Normalize(match ?? _options.IndexHost);
    }

    /// <summary>Accepts the host as Pinecone shows it ("glazeai-2-abc.svc….pinecone.io") or as a URL.</summary>
    private static string Normalize(string host)
    {
        var h = host.Trim().TrimEnd('/');
        if (!h.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !h.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) h = "https://" + h;
        return h + "/";
    }

    /// <summary>Translates the neutral filter into Pinecone's operator syntax. Public for contract tests.</summary>
    public static Dictionary<string, object> TranslateFilter(VectorFilter filter) => filter switch
    {
        VectorFilter.EqualTo eq => new() { [eq.Field] = new Dictionary<string, object> { ["$eq"] = eq.Value } },
        VectorFilter.In @in => new() { [@in.Field] = new Dictionary<string, object> { ["$in"] = @in.Values } },
        VectorFilter.GreaterOrEqual ge => new() { [ge.Field] = new Dictionary<string, object> { ["$gte"] = ge.Value } },
        VectorFilter.LessOrEqual le => new() { [le.Field] = new Dictionary<string, object> { ["$lte"] = le.Value } },
        VectorFilter.And and => new() { ["$and"] = and.Filters.Select(TranslateFilter).ToList() },
        _ => throw new NotSupportedException(filter.GetType().Name)
    };

    private async Task<T?> SendAsync<T>(HttpMethod method, string host, string path, object? body, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            HttpResponseMessage response;
            try
            {
                using var request = new HttpRequestMessage(method, new Uri(new Uri(host), path));
                if (body is not null) request.Content = JsonContent.Create(body, options: Json);
                response = await _http.SendAsync(request, cancellationToken);
            }
            catch (HttpRequestException ex) when (attempt <= _options.MaxRetries)
            {
                await BackoffAsync(attempt, null, cancellationToken);
                _logger.LogWarning(ex, "Pinecone {Path} attempt {Attempt} failed to connect", path, attempt);
                continue;
            }
            catch (HttpRequestException ex) { throw new VectorIndexException(VectorIndexErrorCode.Unavailable, "Pinecone is unreachable.", ex); }

            using (response)
            {
                if (response.IsSuccessStatusCode)
                {
                    if (typeof(T) == typeof(JsonElement?) || response.Content.Headers.ContentLength == 0) return default;
                    return await response.Content.ReadFromJsonAsync<T>(Json, cancellationToken);
                }
                var code = MapStatus(response.StatusCode);
                var detail = await response.Content.ReadAsStringAsync(cancellationToken);
                if (code is VectorIndexErrorCode.RateLimited or VectorIndexErrorCode.Unavailable && attempt <= _options.MaxRetries)
                {
                    _logger.LogWarning("Pinecone {Path} returned {Status} on attempt {Attempt}; retrying", path, (int)response.StatusCode, attempt);
                    await BackoffAsync(attempt, response.Headers.RetryAfter?.Delta, cancellationToken);
                    continue;
                }
                throw new VectorIndexException(code, $"Pinecone {path} failed with {(int)response.StatusCode}: {Truncate(detail)}");
            }
        }
    }

    private async Task BackoffAsync(int attempt, TimeSpan? retryAfter, CancellationToken cancellationToken)
    {
        var delay = retryAfter ?? TimeSpan.FromMilliseconds(_options.RetryBaseMilliseconds * Math.Pow(2, attempt - 1) + Random.Shared.Next(0, 250));
        await Task.Delay(delay, cancellationToken);
    }

    private static VectorIndexErrorCode MapStatus(HttpStatusCode status) => status switch
    {
        HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity => VectorIndexErrorCode.InvalidRequest,
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => VectorIndexErrorCode.Unauthorized,
        HttpStatusCode.NotFound => VectorIndexErrorCode.NotFound,
        HttpStatusCode.TooManyRequests => VectorIndexErrorCode.RateLimited,
        HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout or HttpStatusCode.InternalServerError => VectorIndexErrorCode.Unavailable,
        _ => VectorIndexErrorCode.Unknown
    };

    private static string Truncate(string value) => value.Length > 500 ? value[..500] : value;

    private static IReadOnlyDictionary<string, object>? ToMetadata(Dictionary<string, JsonElement>? metadata)
    {
        if (metadata is null) return null;
        var result = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var (key, element) in metadata)
        {
            object? value = element.ValueKind switch
            {
                JsonValueKind.String => element.GetString(),
                JsonValueKind.Number => element.TryGetInt64(out var l) ? (object)l : element.GetDouble(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Array => element.EnumerateArray().Select(x => x.ToString()).ToList(),
                _ => null
            };
            if (value is not null) result[key] = value;
        }
        return result;
    }

    private sealed record PineconeVector(string Id, float[] Values, IReadOnlyDictionary<string, object>? Metadata);
    private sealed record UpsertResponse(int UpsertedCount);
    private sealed record QueryMatch(string Id, double Score, Dictionary<string, JsonElement>? Metadata);
    private sealed record QueryResponse(List<QueryMatch>? Matches, string? Namespace);
    private sealed record FetchVector(string Id, float[]? Values, Dictionary<string, JsonElement>? Metadata);
    private sealed record FetchResponse(Dictionary<string, FetchVector>? Vectors, string? Namespace);
    private sealed record NamespaceStats(long VectorCount);
    private sealed record StatsResponse(int Dimension, long TotalVectorCount, Dictionary<string, NamespaceStats>? Namespaces);
}
