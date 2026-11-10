using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Glazion.Api.Contracts;
using Glazion.Api.Data;
using Glazion.Api.Domain;
using Glazion.Api.Infrastructure;
using Glazion.Api.Retrieval;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Glazion.Api.Chat;

/// <summary>
/// Conversations (PRD CHAT-01..06). An answer is grounded in the pottery knowledge base and in the API's own tools; the
/// question and the answer are stored together only when the model replied, so a failed send can simply be retried.
/// </summary>
public sealed class ChatService(GlazionDbContext db, IChatModelFactory models, IKnowledgeSource knowledge, ChatTools tools,
    RecipeImageSearchService search, IOptions<ChatOptions> options, ILogger<ChatService> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly ChatOptions _options = options.Value;

    public const string SystemPrompt = """
        You are Glazion's glaze assistant for potters and ceramic artists. Answer questions about glaze chemistry, materials,
        firing, defects (crazing, crawling, pinholing, shivering, running), test tiles and the Glazion apps.

        Ground every answer:
        - Use the knowledge excerpts provided in this conversation when they are relevant, and do not invent facts beyond them
          or well-established ceramic chemistry.
        - Use the tools for anything numerical or account-specific: calculate_umf for chemistry, search_recipe_images to find
          photos of fired tiles, get_recipe for stored recipes, get_upload_status for the user's own uploads, get_material for
          a material's analysis. Never guess a UMF or invent a recipe, tile or upload.
        - The app shows the tile photos from search_recipe_images beside your answer. Refer to a tile as "Tile #<rank>" with
          its recipe code, and describe only the firing, surface and colour the tool returned. Never write image links,
          image markdown or URLs for tiles, photos or uploads.
        - If you are not sure, say so plainly and suggest a test tile or what to check next.
        - Never state that a glaze is food safe; only a lab leach test on the fired ware can show that.

        Write clearly and briefly for a studio potter: short paragraphs or numbered steps, metric amounts, cones as written.
        """;

    /// <summary>
    /// For a regular user (meeting 2026-10-29): Glazion shows matching tiles, not how it finds them, and keeps the
    /// chemistry of its stored recipes and tiles to itself.
    /// </summary>
    public const string UserRules = """
        This user is not an admin. Never explain how Glazion finds or ranks matching tiles: do not say it compares chemistry,
        UMF, oxides or scores, and do not give the chemistry (UMF or oxide values) of Glazion's stored recipes or tiles. If
        asked how matches are found, say Glazion compares each tile's recipe and firing with theirs. You may work out the UMF
        of a recipe the user gives you, and answer general glaze chemistry questions as usual.
        """;

    // ---- conversations ----

    public Task<PagedResult<ConversationSummaryDto>> ListAsync(Guid userId, string? cursor, int? pageSize, CancellationToken cancellationToken) =>
        Paging.PageAsync(db.Conversations.AsNoTracking().Where(x => x.OwnerUserId == userId).OrderByDescending(x => x.UpdatedAt).ThenByDescending(x => x.Id),
            cursor, pageSize, rows => Task.FromResult<IReadOnlyList<ConversationSummaryDto>>(rows.Select(Summary).ToList()), cancellationToken);

    public async Task<ConversationDto> CreateAsync(Guid userId, string? title, CancellationToken cancellationToken)
    {
        var conversation = new Conversation { Id = Guid.CreateVersion7(), OwnerUserId = userId, Title = CleanTitle(title) ?? "New conversation" };
        db.Conversations.Add(conversation);
        await db.SaveChangesAsync(cancellationToken);
        return new ConversationDto(conversation.Id, conversation.Title, conversation.CreatedAt, conversation.UpdatedAt, []);
    }

    public async Task<ConversationDto> GetAsync(Guid userId, Guid conversationId, CancellationToken cancellationToken)
    {
        var conversation = await Owned(userId, conversationId).AsNoTracking().Include(x => x.Messages).SingleOrDefaultAsync(cancellationToken) ?? throw NotFound();
        return new ConversationDto(conversation.Id, conversation.Title, conversation.CreatedAt, conversation.UpdatedAt,
            conversation.Messages.OrderBy(x => x.CreatedAt).ThenBy(x => x.Id).Select(Message).ToList());
    }

    public async Task<ConversationSummaryDto> RenameAsync(Guid userId, Guid conversationId, string? title, CancellationToken cancellationToken)
    {
        var clean = CleanTitle(title) ?? throw ApiProblems.Validation("title", "Give the conversation a title.");
        var conversation = await Owned(userId, conversationId).SingleOrDefaultAsync(cancellationToken) ?? throw NotFound();
        conversation.Title = clean;
        conversation.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return Summary(conversation);
    }

    public async Task DeleteAsync(Guid userId, Guid conversationId, CancellationToken cancellationToken)
    {
        var conversation = await Owned(userId, conversationId).Include(x => x.Messages).SingleOrDefaultAsync(cancellationToken) ?? throw NotFound();
        var messageIds = conversation.Messages.Select(m => m.Id).ToList();
        db.ChatFeedback.RemoveRange(db.ChatFeedback.Where(f => messageIds.Contains(f.MessageId)));
        db.ChatMessages.RemoveRange(conversation.Messages);
        db.Conversations.Remove(conversation);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task RecordFeedbackAsync(Guid userId, ChatFeedbackRequest request, CancellationToken cancellationToken)
    {
        if (request.Feedback is not ("helpful" or "not_helpful")) throw ApiProblems.Validation("feedback", "Feedback must be 'helpful' or 'not_helpful'.");
        var message = await db.ChatMessages.AsNoTracking().Include(x => x.Conversation)
            .SingleOrDefaultAsync(x => x.Id == request.MessageId && x.Conversation.OwnerUserId == userId, cancellationToken)
            ?? throw new ApiProblemException(404, "message_not_found", "Message not found", "No message with this ID is in your conversations.");
        if (message.Role != "assistant") throw ApiProblems.Validation("messageId", "Feedback can only be given on an assistant's answer.");
        var note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim()[..Math.Min(request.Note.Trim().Length, 1000)];
        var existing = await db.ChatFeedback.SingleOrDefaultAsync(x => x.MessageId == message.Id && x.UserId == userId, cancellationToken);
        if (existing is null)
            db.ChatFeedback.Add(new ChatFeedback { Id = Guid.CreateVersion7(), MessageId = message.Id, UserId = userId, Feedback = request.Feedback, Note = note });
        else
        {
            existing.Feedback = request.Feedback;
            existing.Note = note;
            existing.UpdatedAt = DateTimeOffset.UtcNow;
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    // ---- answering ----

    public async Task<SendMessageResponse> SendAsync(SearchRequester requester, Guid conversationId, string? content, ChatTileRef? tile, CancellationToken cancellationToken)
    {
        var question = content?.Trim() ?? string.Empty;
        if (question.Length == 0) throw ApiProblems.Validation("content", "Write a message first.");
        if (question.Length > _options.MaxMessageLength) throw ApiProblems.Validation("content", $"Messages can be up to {_options.MaxMessageLength} characters.");
        var model = models.Create();
        var conversation = await Owned(requester.UserId, conversationId).SingleOrDefaultAsync(cancellationToken) ?? throw NotFound();
        // Asked from a tile: the model is told which one (its recipe id included), the conversation shows only the question.
        var context = tile is null ? null : TileContext(await TileAsync(requester, tile, cancellationToken));

        var history = await db.ChatMessages.AsNoTracking().Where(x => x.ConversationId == conversationId)
            .OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id).Take(Math.Max(0, _options.HistoryMessages)).ToListAsync(cancellationToken);
        history.Reverse();

        var matches = await knowledge.SearchAsync(question, cancellationToken);
        var turns = new List<ChatTurn> { new("system", SystemPrompt) };
        if (!requester.IsAdmin) turns.Add(new("system", UserRules));
        if (matches.Count > 0) turns.Add(new("system", KnowledgeBlock(matches)));
        foreach (var m in history)
        {
            if (m.Context is not null) turns.Add(new("system", m.Context));
            turns.Add(new(m.Role, m.Content));
        }
        if (context is not null) turns.Add(new("system", context));
        turns.Add(new("user", question));

        var toolCalls = new List<ChatToolCallDto>();
        var toolSources = new List<ChatSourceDto>();
        var anyToolSucceeded = false;
        ModelReply reply = null!;
        string? answer = null;
        try
        {
            for (var round = 0; ; round++)
            {
                var allowTools = round < _options.MaxToolRounds;
                reply = await model.CompleteAsync(turns, allowTools ? ChatTools.SpecsFor(requester) : [], cancellationToken);
                if (reply.ToolCalls.Count == 0 || !allowTools)
                {
                    answer = reply.Content?.Trim();
                    break;
                }
                turns.Add(new("assistant", reply.Content, reply.ToolCalls));
                foreach (var call in reply.ToolCalls)
                {
                    var outcome = await tools.RunAsync(call.Name, call.ArgumentsJson, requester, cancellationToken);
                    toolCalls.Add(outcome.Call);
                    toolSources.AddRange(outcome.Sources);
                    anyToolSucceeded |= !outcome.ResultJson.StartsWith("{\"error\"", StringComparison.Ordinal);
                    turns.Add(new("tool", outcome.ResultJson, ToolCallId: call.Id));
                }
            }
        }
        catch (ChatModelException ex)
        {
            logger.LogWarning(ex, "Chat model failed for conversation {ConversationId}", conversationId);
            throw ex.Code == "chat_not_configured"
                ? new ApiProblemException(503, "chat_unavailable", "Chat is not available", "The assistant is not set up yet. Please try again later.")
                : new ApiProblemException(502, "chat_model_unavailable", "The assistant did not answer", "The assistant could not answer just now. Please send your message again.");
        }
        answer = StripInventedLinks(answer);
        if (string.IsNullOrWhiteSpace(answer)) answer = "I couldn't put together an answer to that. Could you rephrase it or add more detail, such as the recipe and cone?";

        var sources = matches.Take(3).Select(m => new ChatSourceDto(m.Id, "knowledge", m.Title, Trim(m.Text, 280)))
            .Concat(toolSources).GroupBy(s => (s.Kind, s.Id)).Select(g => g.First()).Take(8).ToList();
        var confidence = Confidence(matches, anyToolSucceeded);

        var now = DateTimeOffset.UtcNow;
        var userMessage = new ChatMessage { Id = Guid.CreateVersion7(), ConversationId = conversationId, Role = "user", Content = question, Context = context, CreatedAt = now };
        var assistantMessage = new ChatMessage
        {
            Id = Guid.CreateVersion7(), ConversationId = conversationId, Role = "assistant", Content = answer,
            SourcesJson = JsonSerializer.Serialize(sources, Json), ToolCallsJson = JsonSerializer.Serialize(toolCalls, Json),
            Confidence = confidence, Model = reply.Model, CreatedAt = now.AddTicks(1)
        };
        db.ChatMessages.AddRange(userMessage, assistantMessage);
        if (history.Count == 0 && conversation.Title == "New conversation") conversation.Title = TitleFrom(question);
        conversation.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        return new SendMessageResponse(Message(userMessage), Message(assistantMessage), Summary(conversation));
    }

    /// <summary>The result a tile question is about, from one of the requester's own searches.</summary>
    private async Task<SearchResultItem> TileAsync(SearchRequester requester, ChatTileRef tile, CancellationToken cancellationToken)
    {
        var run = await search.GetAsync(tile.SearchId, requester, cancellationToken)
                  ?? throw new ApiProblemException(404, "search_not_found", "Search not found", "No search with this ID belongs to you.");
        return run.Items.Concat(run.Similar ?? []).FirstOrDefault(x => x.ImageId == tile.ImageId)
               ?? throw new ApiProblemException(422, "image_not_in_results", "Image not in results", "Only a tile this search returned can be asked about.");
    }

    /// <summary>What the model is told about a tile asked about from a search: its labels, firing and recipe, with the id for get_recipe.</summary>
    private static string TileContext(SearchResultItem item)
    {
        var looks = string.Join(", ", new[] { item.Tile.ClayBody, item.Tile.Surface, item.Tile.Opacity, item.Tile.ColourFamily }.Where(x => !string.IsNullOrWhiteSpace(x)));
        var text = new StringBuilder($"The user's following message is about a fired test tile from their tile search ({(looks.Length == 0 ? "" : looks + ", ")}{ChatTools.Firing(item.Firing)}; {item.ConfidenceBand} match, {item.Score:0.00}). ");
        if (item.Source.RecipeId is { } recipeId)
        {
            var name = string.Join(" ", new[] { item.Source.RecipeCode, item.Source.RecipeName }.Where(x => !string.IsNullOrWhiteSpace(x)));
            text.Append($"Its glaze is the stored recipe {(name.Length == 0 ? "" : name + " ")}with recipeId {recipeId:D}: look it up with get_recipe before answering. Never write the recipe id in your answer.");
        }
        else text.Append("Its recipe is not shown to this user, so answer from the tile's firing and labels.");
        return text.ToString();
    }

    /// <summary>high: a close knowledge match, or a tool returned real data; medium: some knowledge; low: nothing to ground on (CHAT-06).</summary>
    private string Confidence(IReadOnlyList<KnowledgeMatch> matches, bool toolGrounded)
    {
        var best = matches.Count == 0 ? 0 : matches.Max(m => m.Score);
        if (toolGrounded || best >= _options.Knowledge.HighScore) return "high";
        return matches.Count > 0 ? "medium" : "low";
    }

    private static string KnowledgeBlock(IReadOnlyList<KnowledgeMatch> matches)
    {
        var text = new StringBuilder("Knowledge excerpts from the Glazion pottery knowledge base, most relevant first:\n");
        for (var i = 0; i < matches.Count; i++) text.Append($"\n[{i + 1}] {matches[i].Title}\n{Trim(matches[i].Text, 1500)}\n");
        return text.ToString();
    }

    // ---- mapping ----

    private IQueryable<Conversation> Owned(Guid userId, Guid conversationId) => db.Conversations.Where(x => x.Id == conversationId && x.OwnerUserId == userId);

    private static ApiProblemException NotFound() => new(404, "conversation_not_found", "Conversation not found", "No conversation with this ID is yours.");

    private static ConversationSummaryDto Summary(Conversation c) => new(c.Id, c.Title, c.CreatedAt, c.UpdatedAt);

    private static ChatMessageDto Message(ChatMessage m) => m.Role == "user"
        ? new ChatMessageDto(m.Id, m.Role, m.Content, m.CreatedAt, null, null, null)
        : new ChatMessageDto(m.Id, m.Role, m.Content, m.CreatedAt,
            JsonSerializer.Deserialize<List<ChatSourceDto>>(m.SourcesJson, Json) ?? [],
            JsonSerializer.Deserialize<List<ChatToolCallDto>>(m.ToolCallsJson, Json) ?? [], m.Confidence);

    private string TitleFrom(string question)
    {
        var line = question.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? question;
        if (line.Length <= _options.TitleLength) return line;
        var cut = line[.._options.TitleLength].LastIndexOf(' ');
        return line[..(cut > 20 ? cut : _options.TitleLength)].TrimEnd() + "…";
    }

    private static string? CleanTitle(string? title)
    {
        var clean = title?.Trim();
        return string.IsNullOrEmpty(clean) ? null : clean.Length > 200 ? clean[..200] : clean;
    }

    private static readonly Regex MarkdownImage = new(@"!\[[^\]\n]*\]\([^)\n]*\)", RegexOptions.Compiled);
    private static readonly Regex MarkdownLink = new(@"\[([^\]\n]+)\]\(([^)\s]+)\)", RegexOptions.Compiled);

    /// <summary>
    /// Photos reach the apps through <c>sources</c> and the search ID, never through the answer text, so markdown images
    /// are removed and links to placeholder hosts (example.com and the like) keep only their text. Real links stay.
    /// </summary>
    public static string? StripInventedLinks(string? answer)
    {
        if (string.IsNullOrEmpty(answer)) return answer;
        var text = MarkdownImage.Replace(answer, "");
        text = MarkdownLink.Replace(text, m => IsPlaceholder(m.Groups[2].Value) ? m.Groups[1].Value : m.Value);
        text = Regex.Replace(text, @"[ \t]+\n", "\n");
        return Regex.Replace(text, @"\n{3,}", "\n\n").Trim();
    }

    private static bool IsPlaceholder(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return true; // a relative or made-up target cannot open in the apps
        var host = uri.Host.ToLowerInvariant();
        return host is "example.com" or "example.org" or "example.net" or "localhost" || host.EndsWith(".example", StringComparison.Ordinal)
            || host.EndsWith(".example.com", StringComparison.Ordinal) || host.Contains("placeholder", StringComparison.Ordinal);
    }

    private static string Trim(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";
}

/// <summary>Creates the configured chat model per request, so a missing key fails one send with a clear error instead of startup.</summary>
public interface IChatModelFactory
{
    IChatModel Create();
}

public sealed class ChatModelFactory(IServiceProvider services, IOptions<ChatOptions> options) : IChatModelFactory
{
    public IChatModel Create()
    {
        if (!string.Equals(options.Value.Provider, "OpenAI", StringComparison.OrdinalIgnoreCase))
            throw new ApiProblemException(503, "chat_unavailable", "Chat is not available", "The assistant is not set up yet. Please try again later.");
        try { return services.GetRequiredService<OpenAiChatModel>(); }
        catch (ChatModelException) { throw new ApiProblemException(503, "chat_unavailable", "Chat is not available", "The assistant is not set up yet. Please try again later."); }
    }
}
