using MnaiWork.Api.Agent;
using MnaiWork.Api.Data;
using MnaiWork.Api.Infrastructure;
using MnaiWork.Api.Models;
using MnaiWork.Api.Storage;
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
    private readonly IFileStorage _storage;
    private readonly IAgentRunQueue _queue;
    private readonly ICurrentUser _me;

    public ThreadsController(
        IThreadRepository threads,
        IMessageRepository messages,
        IRunRepository runs,
        IFileStorage storage,
        IAgentRunQueue queue,
        ICurrentUser me)
    {
        _threads = threads;
        _messages = messages;
        _runs = runs;
        _storage = storage;
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

        // Cascade: remove the generated files and all child records, then the thread itself.
        await _storage.DeleteThreadFilesAsync(new ArtifactOwner(_me.Id, threadId), ct);
        await _messages.DeleteByThreadAsync(threadId, ct);
        await _runs.DeleteByThreadAsync(threadId, ct);
        await _threads.DeleteAsync(_me.Id, threadId, ct);
        return NoContent();
    }

    /// <summary>
    /// Mints a fresh download URL for an artifact. The link is generated on demand (not stored),
    /// so it never goes stale, and access is scoped to the caller's own thread.
    /// </summary>
    [HttpGet("{threadId}/artifacts/{artifactId}/download")]
    public async Task<ActionResult<object>> GetArtifactDownloadUrl(string threadId, string artifactId, CancellationToken ct)
    {
        var thread = await _threads.GetAsync(_me.Id, threadId, ct);
        if (thread is null)
        {
            return NotFound();
        }

        // Ownership: the artifact must belong to a message in this (user-owned) thread.
        var messages = await _messages.ListAsync(threadId, ct);
        var artifact = messages.SelectMany(m => m.Artifacts)
            .FirstOrDefault(a => string.Equals(a.Id, artifactId, StringComparison.OrdinalIgnoreCase));
        if (artifact is null)
        {
            return NotFound();
        }

        var url = await _storage.GetDownloadUrlAsync(artifact.BlobPath, artifact.FileName, ct);
        return Ok(new { url });
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
        var hasAttachments = request.Attachments is { Count: > 0 };
        if (string.IsNullOrWhiteSpace(request.Content) && !hasAttachments)
        {
            return BadRequest("Message content or an attachment is required.");
        }

        var thread = await _threads.GetAsync(_me.Id, threadId, ct);
        if (thread is null)
        {
            return NotFound();
        }

        // Only trust attachments whose blob path lives under this user's thread uploads folder.
        var uploadPrefix = $"{Sanitize(_me.Id)}/{Sanitize(threadId)}/uploads/";
        var attachments = (request.Attachments ?? new List<Attachment>())
            .Where(a => a.BlobPath.StartsWith(uploadPrefix, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var sequence = await _messages.GetNextSequenceAsync(threadId, ct);
        var userMessage = new ChatMessage
        {
            ThreadId = threadId,
            Role = MessageRole.User,
            Content = request.Content.Trim(),
            Sequence = sequence,
            Attachments = attachments
        };
        await _messages.AddAsync(userMessage, ct);

        if (thread.Title == "New conversation")
        {
            var title = string.IsNullOrWhiteSpace(userMessage.Content)
                ? (attachments.FirstOrDefault()?.FileName ?? "New conversation")
                : userMessage.Content;
            thread.Title = Truncate(title, 60);
        }
        await _threads.UpsertAsync(thread, ct);

        var run = new AgentRun { ThreadId = threadId, UserId = _me.Id, Status = RunStatus.Queued };
        await _runs.CreateAsync(run, ct);
        await _queue.EnqueueAsync(new AgentRunRequest(run.Id, threadId, _me.Id), ct);

        return Ok(new SendMessageResponse(run.Id, threadId, userMessage.Id));
    }

    /// <summary>Uploads a file (image / pdf / docx / pptx) to this thread. Returns attachment metadata.</summary>
    [HttpPost("{threadId}/uploads")]
    [RequestSizeLimit(30_000_000)]
    public async Task<ActionResult<Attachment>> Upload(string threadId, IFormFile file, CancellationToken ct)
    {
        var thread = await _threads.GetAsync(_me.Id, threadId, ct);
        if (thread is null)
        {
            return NotFound();
        }
        if (file is null || file.Length == 0)
        {
            return BadRequest("A non-empty file is required.");
        }
        if (file.Length > 30_000_000)
        {
            return BadRequest("File is too large (max 30 MB).");
        }

        var kind = ClassifyAttachment(file.FileName, file.ContentType);
        if (kind == AttachmentKind.Other)
        {
            return BadRequest("Unsupported file type. Allowed: images, PDF, DOCX, PPTX.");
        }

        await using var ms = new MemoryStream();
        await file.CopyToAsync(ms, ct);
        var bytes = ms.ToArray();

        var owner = new ArtifactOwner(_me.Id, threadId);
        var contentType = string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType;
        var attachment = await _storage.UploadUserFileAsync(owner, file.FileName, kind, bytes, contentType, ct);
        return Ok(attachment);
    }

    private static AttachmentKind ClassifyAttachment(string fileName, string? contentType)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        var ct = (contentType ?? string.Empty).ToLowerInvariant();
        if (ct.StartsWith("image/") || ext is ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp")
            return AttachmentKind.Image;
        if (ct == "application/pdf" || ext == ".pdf") return AttachmentKind.Pdf;
        if (ext == ".docx" || ct.Contains("wordprocessingml")) return AttachmentKind.Docx;
        if (ext == ".pptx" || ct.Contains("presentationml")) return AttachmentKind.Pptx;
        return AttachmentKind.Other;
    }

    private static string Sanitize(string value)
    {
        var cleaned = new string((value ?? string.Empty)
            .Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray());
        return string.IsNullOrEmpty(cleaned) ? "unknown" : cleaned;
    }

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..max].TrimEnd() + "\u2026";
}
