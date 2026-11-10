namespace Glazion.Api.Chat;

// Shapes match @glazion/types main.ts (Conversation, ChatMessage, ChatSource, ChatToolCall, SendMessage*, ChatFeedbackRequest).

public sealed record ConversationSummaryDto(Guid Id, string Title, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record ConversationDto(Guid Id, string Title, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, IReadOnlyList<ChatMessageDto> Messages);

/// <summary>kind: knowledge | recipe | material | image.</summary>
public sealed record ChatSourceDto(string Id, string Kind, string Title, string Snippet);

/// <summary>name: calculate_umf | search_recipe_images | get_recipe | get_upload_status | get_material. searchId is set for searches.</summary>
public sealed record ChatToolCallDto(string Name, string Summary, Guid? SearchId = null);

/// <summary>role: user | assistant. confidence: high | medium | low (assistant messages only).</summary>
public sealed record ChatMessageDto(Guid Id, string Role, string Content, DateTimeOffset CreatedAt,
    IReadOnlyList<ChatSourceDto>? Sources, IReadOnlyList<ChatToolCallDto>? ToolCalls, string? Confidence);

public sealed record CreateConversationRequest(string? Title);
public sealed record RenameConversationRequest(string? Title);
/// <summary>tile: a result of one of the user's own searches that the message is about. The model is told its recipe and labels; the conversation shows only the content.</summary>
public sealed record SendMessageRequest(string? Content, ChatTileRef? Tile = null);
public sealed record ChatTileRef(Guid SearchId, Guid ImageId);
public sealed record SendMessageResponse(ChatMessageDto UserMessage, ChatMessageDto AssistantMessage, ConversationSummaryDto Conversation);
public sealed record ChatFeedbackRequest(Guid MessageId, string? Feedback, string? Note);
