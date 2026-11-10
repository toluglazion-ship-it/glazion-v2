using Glazion.Api.Chat;
using Glazion.Api.Contracts;
using Glazion.Api.Infrastructure;
using Glazion.Api.Retrieval;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Glazion.Api.Controllers;

/// <summary>Chat (PRD CHAT-01..06), in the shapes the apps' chatApi uses. Every conversation is private to the user who started it.</summary>
[ApiController]
[Route("api/v2")]
[Authorize]
public sealed class ConversationsController(ChatService chat) : ControllerBase
{
    [HttpGet("conversations")]
    public Task<PagedResult<ConversationSummaryDto>> List([FromQuery] string? cursor, [FromQuery] int? pageSize, CancellationToken cancellationToken) =>
        chat.ListAsync(User.GetUserId(), cursor, pageSize, cancellationToken);

    [HttpPost("conversations")]
    public async Task<ActionResult<ConversationDto>> Create([FromBody(EmptyBodyBehavior = Microsoft.AspNetCore.Mvc.ModelBinding.EmptyBodyBehavior.Allow)] CreateConversationRequest? request, CancellationToken cancellationToken)
    {
        var conversation = await chat.CreateAsync(User.GetUserId(), request?.Title, cancellationToken);
        return Created($"/api/v2/conversations/{conversation.Id}", conversation);
    }

    [HttpGet("conversations/{conversationId:guid}")]
    public async Task<ActionResult<ConversationDto>> Get(Guid conversationId, CancellationToken cancellationToken) =>
        Ok(await chat.GetAsync(User.GetUserId(), conversationId, cancellationToken));

    /// <summary>Asks the assistant. Returns the stored question, the answer (with sources, tool calls and confidence) and the conversation, which may have been titled from its first message.</summary>
    [HttpPost("conversations/{conversationId:guid}/messages")]
    public async Task<ActionResult<SendMessageResponse>> Send(Guid conversationId, SendMessageRequest request, CancellationToken cancellationToken) =>
        Ok(await chat.SendAsync(new SearchRequester(User.GetUserId(), User.IsAdmin()), conversationId, request.Content, request.Tile, cancellationToken));

    [HttpPatch("conversations/{conversationId:guid}")]
    public async Task<ActionResult<ConversationSummaryDto>> Rename(Guid conversationId, RenameConversationRequest request, CancellationToken cancellationToken) =>
        Ok(await chat.RenameAsync(User.GetUserId(), conversationId, request.Title, cancellationToken));

    [HttpDelete("conversations/{conversationId:guid}")]
    public async Task<IActionResult> Delete(Guid conversationId, CancellationToken cancellationToken)
    {
        await chat.DeleteAsync(User.GetUserId(), conversationId, cancellationToken);
        return NoContent();
    }

    [HttpPost("chat/feedback")]
    public async Task<IActionResult> Feedback(ChatFeedbackRequest request, CancellationToken cancellationToken)
    {
        await chat.RecordFeedbackAsync(User.GetUserId(), request, cancellationToken);
        return NoContent();
    }
}
