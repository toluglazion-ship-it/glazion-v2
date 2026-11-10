using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace Glazion.Api.Chat;

/// <summary>One message sent to the model: system, user, assistant (optionally with tool calls) or a tool result.</summary>
public sealed record ChatTurn(string Role, string? Content, IReadOnlyList<ModelToolCall>? ToolCalls = null, string? ToolCallId = null);

public sealed record ModelToolCall(string Id, string Name, string ArgumentsJson);

/// <summary>A function the model may call; Parameters is a JSON Schema object.</summary>
public sealed record ToolSpec(string Name, string Description, JsonObject Parameters);

public sealed record ModelReply(string? Content, IReadOnlyList<ModelToolCall> ToolCalls, string Model);

public sealed class ChatModelException(string code, string message, Exception? inner = null) : Exception(message, inner)
{
    public string Code { get; } = code;
}

public interface IChatModel
{
    string Model { get; }
    Task<ModelReply> CompleteAsync(IReadOnlyList<ChatTurn> turns, IReadOnlyList<ToolSpec> tools, CancellationToken cancellationToken);
}

/// <summary>OpenAI chat completions with function calling (also works with OpenAI-compatible gateways). Retries 429/5xx; the key is never logged.</summary>
public sealed class OpenAiChatModel : IChatModel
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http;
    private readonly ChatModelOptions _options;
    private readonly ILogger<OpenAiChatModel> _logger;

    public OpenAiChatModel(HttpClient http, IOptions<ChatOptions> options, ILogger<OpenAiChatModel> logger)
    {
        _http = http;
        _options = options.Value.OpenAI;
        _logger = logger;
        if (string.IsNullOrWhiteSpace(_options.ApiKey)) throw new ChatModelException("chat_not_configured", "Chat is not configured. Set Chat__OpenAI__ApiKey.");
        _http.BaseAddress = new Uri(_options.BaseUrl.TrimEnd('/') + "/");
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        _http.Timeout = TimeSpan.FromSeconds(Math.Max(10, _options.TimeoutSeconds));
    }

    public string Model => _options.Model;

    public async Task<ModelReply> CompleteAsync(IReadOnlyList<ChatTurn> turns, IReadOnlyList<ToolSpec> tools, CancellationToken cancellationToken)
    {
        var body = BuildRequest(turns, tools);
        for (var attempt = 1; ; attempt++)
        {
            HttpResponseMessage response;
            try { response = await _http.PostAsync("chat/completions", new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json"), cancellationToken); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                if (attempt <= _options.MaxRetries) { await Task.Delay(Backoff(attempt), cancellationToken); continue; }
                throw new ChatModelException("chat_model_unavailable", "The chat model could not be reached.", ex);
            }
            using (response)
            {
                var text = await response.Content.ReadAsStringAsync(cancellationToken);
                if (response.IsSuccessStatusCode) return Parse(text);
                var transient = response.StatusCode is HttpStatusCode.TooManyRequests or >= HttpStatusCode.InternalServerError;
                if (transient && attempt <= _options.MaxRetries)
                {
                    _logger.LogWarning("Chat model returned {Status} on attempt {Attempt}; retrying", (int)response.StatusCode, attempt);
                    await Task.Delay(response.Headers.RetryAfter?.Delta ?? Backoff(attempt), cancellationToken);
                    continue;
                }
                _logger.LogError("Chat model request failed with {Status}: {Body}", (int)response.StatusCode, text.Length > 500 ? text[..500] : text);
                throw new ChatModelException(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden ? "chat_not_configured" : "chat_model_unavailable",
                    $"The chat model returned {(int)response.StatusCode}.");
            }
        }
    }

    private JsonObject BuildRequest(IReadOnlyList<ChatTurn> turns, IReadOnlyList<ToolSpec> tools)
    {
        var messages = new JsonArray();
        foreach (var turn in turns)
        {
            var message = new JsonObject { ["role"] = turn.Role, ["content"] = turn.Content };
            if (turn.ToolCalls is { Count: > 0 })
                message["tool_calls"] = new JsonArray(turn.ToolCalls.Select(c => (JsonNode)new JsonObject
                {
                    ["id"] = c.Id, ["type"] = "function",
                    ["function"] = new JsonObject { ["name"] = c.Name, ["arguments"] = c.ArgumentsJson }
                }).ToArray());
            if (turn.ToolCallId is not null) message["tool_call_id"] = turn.ToolCallId;
            messages.Add(message);
        }
        var request = new JsonObject { ["model"] = _options.Model, ["messages"] = messages, ["max_completion_tokens"] = _options.MaxOutputTokens };
        // Reasoning models (o-series, gpt-5) reject a custom temperature
        if (!_options.Model.StartsWith('o') && !_options.Model.StartsWith("gpt-5", StringComparison.OrdinalIgnoreCase)) request["temperature"] = _options.Temperature;
        if (tools.Count > 0)
        {
            request["tools"] = new JsonArray(tools.Select(t => (JsonNode)new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject { ["name"] = t.Name, ["description"] = t.Description, ["parameters"] = t.Parameters.DeepClone() }
            }).ToArray());
            request["tool_choice"] = "auto";
        }
        return request;
    }

    private ModelReply Parse(string text)
    {
        var root = JsonNode.Parse(text) ?? throw new ChatModelException("chat_model_unavailable", "Empty chat model response.");
        var message = root["choices"]?[0]?["message"] ?? throw new ChatModelException("chat_model_unavailable", "The chat model returned no message.");
        var calls = (message["tool_calls"] as JsonArray ?? [])
            .Select(c => new ModelToolCall(c?["id"]?.GetValue<string>() ?? Guid.NewGuid().ToString("N"),
                c?["function"]?["name"]?.GetValue<string>() ?? string.Empty,
                c?["function"]?["arguments"]?.GetValue<string>() ?? "{}"))
            .Where(c => c.Name.Length > 0).ToList();
        return new ModelReply(message["content"]?.GetValue<string>(), calls, root["model"]?.GetValue<string>() ?? _options.Model);
    }

    private static TimeSpan Backoff(int attempt) => TimeSpan.FromMilliseconds(500 * Math.Pow(2, attempt - 1) + Random.Shared.Next(0, 250));
}
