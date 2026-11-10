using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Glazion.Api.Indexing;

public sealed class EmbeddingOptions
{
    /// <summary>"OpenAI" (any OpenAI-compatible /embeddings endpoint) or "Hashing" (deterministic local fallback).</summary>
    public string Provider { get; set; } = "Hashing";
    public OpenAiEmbeddingOptions OpenAI { get; set; } = new();
    public HashingEmbeddingOptions Hashing { get; set; } = new();
}

public sealed class OpenAiEmbeddingOptions
{
    public string BaseUrl { get; set; } = "https://api.openai.com/v1";
    public string ApiKey { get; set; } = string.Empty;
    public string Model { get; set; } = "text-embedding-3-small";
    /// <summary>Pinned version label stored with every vector, e.g. "text-embedding-3-small:2026-09". Change it to force a re-index.</summary>
    public string Version { get; set; } = "text-embedding-3-small:2026-09";
    public int Dimension { get; set; } = 1536;
    public int BatchSize { get; set; } = 64;
    public int MaxRetries { get; set; } = 4;
}

public sealed class HashingEmbeddingOptions
{
    public int Dimension { get; set; } = 256;
}

/// <summary>Produces one vector per input text. Model and Version are stored with each vector so a change forces re-indexing.</summary>
public interface ITextEmbeddingProvider
{
    string Model { get; }
    string Version { get; }
    int Dimension { get; }
    Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken);
}

/// <summary>
/// Deterministic feature-hashing embedding for local development and tests. It is NOT a semantic embedding and is
/// labelled as such in its version string so an evaluation can never mistake it for a model. Tokens are hashed
/// into a fixed number of buckets with signed counts, then L2-normalised; identical canonical text yields an
/// identical vector, and texts sharing lines score higher than unrelated ones, which is enough to exercise the
/// pipeline end to end.
/// </summary>
public sealed class HashingTextEmbeddingProvider(IOptions<EmbeddingOptions> options) : ITextEmbeddingProvider
{
    private readonly int _dimension = Math.Max(16, options.Value.Hashing.Dimension);
    public string Model => "feature-hashing";
    public string Version => $"hashing-{_dimension}:v1";
    public int Dimension => _dimension;

    public Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<float[]>>(texts.Select(Embed).ToList());

    private float[] Embed(string text)
    {
        var vector = new float[_dimension];
        foreach (var token in Tokenize(text))
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
            var bucket = (int)(BitConverter.ToUInt32(hash, 0) % (uint)_dimension);
            var sign = (hash[4] & 1) == 0 ? 1f : -1f;
            vector[bucket] += sign;
        }
        var norm = Math.Sqrt(vector.Sum(v => (double)v * v));
        if (norm > 0) for (var i = 0; i < vector.Length; i++) vector[i] = (float)(vector[i] / norm);
        return vector;
    }

    private static IEnumerable<string> Tokenize(string text)
    {
        foreach (var rawLine in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;
            yield return "line:" + line;                       // whole line as one feature
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var key = line[..colon].Trim();
            foreach (var part in line[(colon + 1)..].Split([';', ' '], StringSplitOptions.RemoveEmptyEntries))
                yield return key + "=" + part.Trim().ToLowerInvariant(); // key-scoped value features
        }
    }
}

/// <summary>Any OpenAI-compatible embeddings endpoint (OpenAI, Azure-style gateways, local servers). Batched, retried on 429/5xx, key never logged.</summary>
public sealed class OpenAiCompatibleTextEmbeddingProvider : ITextEmbeddingProvider
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http;
    private readonly OpenAiEmbeddingOptions _options;
    private readonly ILogger<OpenAiCompatibleTextEmbeddingProvider> _logger;

    public OpenAiCompatibleTextEmbeddingProvider(HttpClient http, IOptions<EmbeddingOptions> options, ILogger<OpenAiCompatibleTextEmbeddingProvider> logger)
    {
        _http = http;
        _options = options.Value.OpenAI;
        _logger = logger;
        if (string.IsNullOrWhiteSpace(_options.ApiKey)) throw new InvalidOperationException("Embeddings are not configured. Set Embeddings__OpenAI__ApiKey.");
        _http.BaseAddress = new Uri(_options.BaseUrl.TrimEnd('/') + "/");
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
    }

    public string Model => _options.Model;
    public string Version => _options.Version;
    public int Dimension => _options.Dimension;

    public async Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken)
    {
        var result = new List<float[]>(texts.Count);
        foreach (var batch in texts.Chunk(Math.Max(1, _options.BatchSize)))
        {
            var response = await SendAsync(batch, cancellationToken);
            var ordered = response.Data.OrderBy(x => x.Index).Select(x => x.Embedding).ToList();
            if (ordered.Count != batch.Length) throw new VectorIndexException(VectorIndexErrorCode.Unknown, $"Embedding provider returned {ordered.Count} vectors for {batch.Length} inputs.");
            foreach (var vector in ordered)
            {
                if (vector.Length != _options.Dimension) throw new VectorIndexException(VectorIndexErrorCode.InvalidRequest, $"Embedding dimension {vector.Length} does not match configured {_options.Dimension}.");
                result.Add(vector);
            }
        }
        return result;
    }

    private async Task<EmbeddingResponse> SendAsync(string[] inputs, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            HttpResponseMessage response;
            try { response = await _http.PostAsJsonAsync("embeddings", new { model = _options.Model, input = inputs }, Json, cancellationToken); }
            catch (HttpRequestException ex) when (attempt <= _options.MaxRetries) { await Task.Delay(Backoff(attempt), cancellationToken); _logger.LogWarning(ex, "Embedding request attempt {Attempt} failed to connect", attempt); continue; }
            catch (HttpRequestException ex) { throw new VectorIndexException(VectorIndexErrorCode.Unavailable, "Embedding provider is unreachable.", ex); }
            using (response)
            {
                if (response.IsSuccessStatusCode)
                    return await response.Content.ReadFromJsonAsync<EmbeddingResponse>(Json, cancellationToken) ?? throw new VectorIndexException(VectorIndexErrorCode.Unknown, "Empty embedding response.");
                var transient = response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;
                if (transient && attempt <= _options.MaxRetries)
                {
                    _logger.LogWarning("Embedding provider returned {Status} on attempt {Attempt}; retrying", (int)response.StatusCode, attempt);
                    await Task.Delay(response.Headers.RetryAfter?.Delta ?? Backoff(attempt), cancellationToken);
                    continue;
                }
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                var code = transient ? VectorIndexErrorCode.Unavailable : response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden ? VectorIndexErrorCode.Unauthorized : VectorIndexErrorCode.InvalidRequest;
                throw new VectorIndexException(code, $"Embedding request failed with {(int)response.StatusCode}: {(body.Length > 500 ? body[..500] : body)}");
            }
        }
    }

    private static TimeSpan Backoff(int attempt) => TimeSpan.FromMilliseconds(500 * Math.Pow(2, attempt - 1) + Random.Shared.Next(0, 250));

    private sealed record EmbeddingDatum(int Index, float[] Embedding);
    private sealed record EmbeddingResponse(List<EmbeddingDatum> Data);
}
