using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace Glazion.Api.Chat;

public sealed record KnowledgeMatch(string Id, string Title, string Text, double Score);

/// <summary>Finds pottery knowledge entries relevant to a question. Read only.</summary>
public interface IKnowledgeSource
{
    bool IsConfigured { get; }
    Task<IReadOnlyList<KnowledgeMatch>> SearchAsync(string question, CancellationToken cancellationToken);
}

public sealed class NoKnowledgeSource : IKnowledgeSource
{
    public bool IsConfigured => false;
    public Task<IReadOnlyList<KnowledgeMatch>> SearchAsync(string question, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<KnowledgeMatch>>([]);
}

/// <summary>
/// Pottery knowledge in Pinecone: the first build's index plus any further namespaces or indexes configured. Embeds
/// the question once per embedding model, queries every source in parallel, and keeps the best matches overall,
/// reading each entry's title and body from its metadata. Never writes to an index. A failing source is skipped; if
/// all fail the answer is given without knowledge (marked low confidence) instead of failing the chat.
/// </summary>
public sealed class PineconeKnowledgeSource : IKnowledgeSource
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http;
    private readonly KnowledgeOptions _options;
    private readonly string _embeddingBaseUrl;
    private readonly string _embeddingKey;
    private readonly ILogger<PineconeKnowledgeSource> _logger;

    public PineconeKnowledgeSource(HttpClient http, IOptions<ChatOptions> options, ILogger<PineconeKnowledgeSource> logger)
    {
        _http = http;
        _logger = logger;
        _options = options.Value.Knowledge;
        _embeddingBaseUrl = (string.IsNullOrWhiteSpace(_options.EmbeddingBaseUrl) ? options.Value.OpenAI.BaseUrl : _options.EmbeddingBaseUrl).TrimEnd('/');
        _embeddingKey = string.IsNullOrWhiteSpace(_options.EmbeddingApiKey) ? options.Value.OpenAI.ApiKey : _options.EmbeddingApiKey;
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_options.PineconeApiKey) && !string.IsNullOrWhiteSpace(_embeddingKey) && Targets().Count > 0;

    /// <summary>Every (index host, namespace, embedding model) to search, without duplicates.</summary>
    public IReadOnlyList<(string Host, string Namespace, string Model)> Targets()
    {
        var targets = new List<(string Host, string Namespace, string Model)>();
        void Add(string? host, string? ns, string? model)
        {
            if (string.IsNullOrWhiteSpace(host)) return;
            var t = (Normalize(host), ns?.Trim() ?? string.Empty, string.IsNullOrWhiteSpace(model) ? _options.EmbeddingModel : model.Trim());
            if (!targets.Contains(t)) targets.Add(t);
        }
        Add(_options.PineconeIndexHost, _options.Namespace, null);
        foreach (var ns in _options.Namespaces.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) Add(_options.PineconeIndexHost, ns, null);
        foreach (var source in _options.Sources) Add(source.IndexHost, source.Namespace, source.EmbeddingModel);
        return targets;
    }

    public async Task<IReadOnlyList<KnowledgeMatch>> SearchAsync(string question, CancellationToken cancellationToken)
    {
        if (!IsConfigured || string.IsNullOrWhiteSpace(question)) return [];
        var targets = Targets();
        var vectors = new Dictionary<string, float[]>(StringComparer.Ordinal);
        foreach (var model in targets.Select(t => t.Model).Distinct(StringComparer.Ordinal))
        {
            try { vectors[model] = await EmbedAsync(question, model, cancellationToken); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException && !cancellationToken.IsCancellationRequested)
            { _logger.LogWarning(ex, "Embedding the question with {Model} failed; its knowledge sources are skipped", model); }
        }
        var topK = Math.Clamp(_options.TopK, 1, 20);
        var results = await Task.WhenAll(targets.Where(t => vectors.ContainsKey(t.Model)).Select(t => QueryAsync(t.Host, t.Namespace, vectors[t.Model], topK, cancellationToken)));
        return results.SelectMany(x => x)
            .Where(m => m.Score >= _options.MinScore && m.Text.Length > 0)
            .GroupBy(m => m.Text, StringComparer.Ordinal).Select(g => g.OrderByDescending(m => m.Score).First()) // the same entry copied into two places counts once
            .OrderByDescending(m => m.Score)
            .Take(topK)
            .ToList();
    }

    private async Task<IReadOnlyList<KnowledgeMatch>> QueryAsync(string host, string ns, float[] vector, int topK, CancellationToken cancellationToken)
    {
        try
        {
            using var query = new HttpRequestMessage(HttpMethod.Post, host + "query")
            {
                Content = JsonContent.Create(new { vector, topK, includeMetadata = true, @namespace = ns }, options: Json)
            };
            query.Headers.Add("Api-Key", _options.PineconeApiKey);
            query.Headers.Add("X-Pinecone-API-Version", _options.PineconeApiVersion);
            using var response = await _http.SendAsync(query, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Knowledge query on {Host} namespace '{Namespace}' returned {Status}", host, ns, (int)response.StatusCode);
                return [];
            }
            var root = await response.Content.ReadFromJsonAsync<JsonObject>(Json, cancellationToken);
            return (root?["matches"] as JsonArray ?? []).Select(ToMatch).Where(m => m is not null).Select(m => m!).ToList();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Knowledge search on {Host} namespace '{Namespace}' failed; skipped", host, ns);
            return [];
        }
    }

    private async Task<float[]> EmbedAsync(string text, string model, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _embeddingBaseUrl + "/embeddings")
        {
            Content = JsonContent.Create(new { model, input = text }, options: Json)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _embeddingKey);
        using var response = await _http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var root = await response.Content.ReadFromJsonAsync<JsonObject>(Json, cancellationToken);
        return root?["data"]?[0]?["embedding"]?.Deserialize<float[]>(Json) ?? throw new JsonException("Embedding response had no vector.");
    }

    private KnowledgeMatch? ToMatch(JsonNode? match)
    {
        if (match is null) return null;
        var metadata = match["metadata"] as JsonObject;
        string Field(IEnumerable<string> names) => names.Select(n => metadata?[n] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null)
            .FirstOrDefault(s => !string.IsNullOrWhiteSpace(s))?.Trim() ?? string.Empty;
        var text = Field(_options.TextFields);
        var title = Field(_options.TitleFields);
        return new KnowledgeMatch(match["id"]?.GetValue<string>() ?? string.Empty,
            title.Length > 0 ? title : (text.Length > 60 ? text[..60] + "…" : text), text,
            match["score"]?.GetValue<double>() ?? 0);
    }

    private static string Normalize(string host)
    {
        var h = host.Trim();
        return (h.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? h : "https://" + h).TrimEnd('/') + "/";
    }
}
