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

    public FilesController(IFileStorage storage) => _storage = storage;

    [HttpGet("{**blobPath}")]
    public async Task<IActionResult> Download(string blobPath, CancellationToken ct)
    {
        var result = await _storage.OpenReadAsync(blobPath, ct);
        if (result is null)
        {
            return NotFound();
        }

        var (stream, contentType, fileName) = result.Value;
        return File(stream, contentType, fileName);
    }
}
