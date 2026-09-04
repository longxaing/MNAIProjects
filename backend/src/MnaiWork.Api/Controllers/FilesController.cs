using MnaiWork.Api.Data;
using MnaiWork.Api.Infrastructure;
using MnaiWork.Api.Models;
using MnaiWork.Api.Storage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MnaiWork.Api.Controllers;

/// <summary>
/// Backend-proxied download of a stored artifact. Used only when a direct SAS link cannot be
/// generated (e.g. SAS disabled on the storage account).
/// </summary>
[ApiController]
[Authorize]
[Route("api/files")]
public sealed class FilesController : ControllerBase
{
    private readonly IFileStorage _storage;
    private readonly IThreadRepository _threads;
    private readonly IMessageRepository _messages;
    private readonly ICurrentUser _me;

    public FilesController(
        IFileStorage storage,
        IThreadRepository threads,
        IMessageRepository messages,
        ICurrentUser me)
    {
        _storage = storage;
        _threads = threads;
        _messages = messages;
        _me = me;
    }

    [HttpGet("{**blobPath}")]
    public async Task<IActionResult> Download(string blobPath, CancellationToken ct)
    {
        var segments = blobPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 3)
        {
            return NotFound();
        }
        var threadId = segments[1];
        if (await _threads.GetAsync(_me.Id, threadId, ct) is null)
        {
            return NotFound();
        }
        var messages = await _messages.ListAsync(threadId, ct);
        if (!OwnsArtifact(messages, blobPath))
        {
            return NotFound();
        }

        var result = await _storage.OpenReadAsync(blobPath, ct);
        if (result is null)
        {
            return NotFound();
        }

        var (stream, contentType, fileName) = result.Value;
        return File(stream, contentType, fileName);
    }

    internal static bool OwnsArtifact(
        IEnumerable<ChatMessage> messages,
        string blobPath) => messages.SelectMany(message => message.Artifacts)
        .Any(artifact => string.Equals(
            artifact.BlobPath, blobPath, StringComparison.Ordinal));
}
