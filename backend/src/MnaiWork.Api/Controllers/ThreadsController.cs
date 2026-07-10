using MnaiWork.Api.Agent;
using MnaiWork.Api.Data;
using MnaiWork.Api.Infrastructure;
using MnaiWork.Api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MnaiWork.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/threads")]
public sealed class ThreadsController : ControllerBase
{
    private readonly IThreadRepository _threads;
    private readonly IMessageRepository _messages;
    private readonly IRunRepository _runs;
    private readonly IAgentRunQueue _queue;
    private readonly ICurrentUser _me;

    public ThreadsController(
        IThreadRepository threads,
        IMessageRepository messages,
        IRunRepository runs,
        IAgentRunQueue queue,
        ICurrentUser me)
    {
        _threads = threads;
        _messages = messages;
        _runs = runs;
        _queue = queue;
        _me = me;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ThreadListItem>>> List(CancellationToken ct)
        => Ok(await _threads.ListAsync(_me.Id, ct));

    [HttpPost]
    public async Task<ActionResult<ChatThread>> Create([FromBody] CreateThreadRequest request, CancellationToken ct)
    {
        var thread = new ChatThread
        {
            UserId = _me.Id,
            Title = string.IsNullOrWhiteSpace(request.Title) ? "New conversation" : request.Title!.Trim()
        };
        return Ok(await _threads.CreateAsync(thread, ct));
    }

    [HttpGet("{threadId}")]
    public async Task<ActionResult<ChatThread>> Get(string threadId, CancellationToken ct)
    {
        var thread = await _threads.GetAsync(_me.Id, threadId, ct);
        return thread is null ? NotFound() : Ok(thread);
    }

    [HttpDelete("{threadId}")]
    public async Task<IActionResult> Delete(string threadId, CancellationToken ct)
    {
        var thread = await _threads.GetAsync(_me.Id, threadId, ct);
        if (thread is null)
        {
            return NotFound();
        }
        await _threads.DeleteAsync(_me.Id, threadId, ct);
        return NoContent();
    }

    [HttpGet("{threadId}/messages")]
    public async Task<ActionResult<IReadOnlyList<ChatMessage>>> Messages(string threadId, CancellationToken ct)
    {
        var thread = await _threads.GetAsync(_me.Id, threadId, ct);
        if (thread is null)
        {
            return NotFound();
        }
        return Ok(await _messages.ListAsync(threadId, ct));
    }

    /// <summary>Adds a user message and starts an agent run. Returns immediately with the run id.</summary>
    [HttpPost("{threadId}/messages")]
    public async Task<ActionResult<SendMessageResponse>> Send(
        string threadId, [FromBody] SendMessageRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Content))
        {
            return BadRequest("Message content is required.");
        }

        var thread = await _threads.GetAsync(_me.Id, threadId, ct);
        if (thread is null)
        {
            return NotFound();
        }

        var sequence = await _messages.GetNextSequenceAsync(threadId, ct);
        var userMessage = new ChatMessage
        {
            ThreadId = threadId,
            Role = MessageRole.User,
            Content = request.Content.Trim(),
            Sequence = sequence
        };
        await _messages.AddAsync(userMessage, ct);

        if (thread.Title == "New conversation")
        {
            thread.Title = Truncate(userMessage.Content, 60);
        }
        await _threads.UpsertAsync(thread, ct);

        var run = new AgentRun { ThreadId = threadId, UserId = _me.Id, Status = RunStatus.Queued };
        await _runs.CreateAsync(run, ct);
        await _queue.EnqueueAsync(new AgentRunRequest(run.Id, threadId, _me.Id), ct);

        return Ok(new SendMessageResponse(run.Id, threadId, userMessage.Id));
    }

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..max].TrimEnd() + "\u2026";
}
