using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.RateLimiting;
using MnaiWork.Api.Infrastructure;
using MnaiWork.Api.Sharing;

namespace MnaiWork.Api.Controllers;

public sealed class ShareResponseAttribute : ActionFilterAttribute, IExceptionFilter
{
    public override void OnActionExecuting(ActionExecutingContext context)
    {
        var headers = context.HttpContext.Response.Headers;
        headers.CacheControl = "no-store, max-age=0";
        headers["Referrer-Policy"] = "no-referrer";
        headers["X-Robots-Tag"] = "noindex, nofollow, noarchive";
        headers["X-Content-Type-Options"] = "nosniff";
        headers["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'; sandbox";
    }

    public void OnException(ExceptionContext context)
    {
        if (context.Exception is KeyNotFoundException)
            context.Result = new NotFoundObjectResult(new { error = "Shared conversation unavailable." });
        else if (context.Exception is InvalidDataException)
            context.Result = new BadRequestObjectResult(new { error = context.Exception.Message });
        else if (context.Exception is InvalidOperationException)
            context.Result = new ConflictObjectResult(new { error = context.Exception.Message });
        else return;
        context.ExceptionHandled = true;
    }
}

[ApiController]
[Authorize(Policy = ShareAuthorization.OwnerPolicy)]
[ShareResponse]
[EnableRateLimiting("share-management")]
[Route("api/threads/{threadId}/shares")]
public sealed class SharesController(ShareService shares, ICurrentUser me) : ControllerBase
{
    [HttpPost("preview")]
    [RequestSizeLimit(16_384)]
    public async Task<ActionResult<SharePreview>> Preview(string threadId, PreviewShareRequest request, CancellationToken ct)
        => Ok(await shares.PreviewAsync(me.Id, threadId, request, ct));

    [HttpPost]
    [RequestSizeLimit(4096)]
    public async Task<IActionResult> Publish(string threadId, PublishShareRequest request, CancellationToken ct)
    {
        await shares.PublishAsync(me.Id, threadId, request.PreviewId, ct);
        return Ok(new { path = "#/share/" + Uri.EscapeDataString(threadId), id = ShareService.PublicId(threadId) });
    }

    [HttpGet]
    public async Task<IActionResult> List(string threadId, CancellationToken ct)
        => Ok(await shares.ListAsync(me.Id, threadId, ct));

    [HttpDelete("{shareId}")]
    public async Task<IActionResult> Revoke(string threadId, string shareId, CancellationToken ct)
    {
        await shares.RevokeAsync(me.Id, threadId, shareId, ct);
        return NoContent();
    }

    [HttpGet("preview/{previewId}/images/{imageId}")]
    public async Task<IActionResult> PreviewImage(string threadId, string previewId, string imageId, CancellationToken ct)
        => File(await shares.PreviewImageAsync(me.Id, threadId, previewId, imageId, ct), "image/png");
}

[ApiController]
[AllowAnonymous]
[ShareResponse]
[EnableRateLimiting("share-public")]
[Route("api/shared/{threadId}")]
public sealed class SharedConversationController(ShareService shares) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<SharedSnapshot>> Read(string threadId, CancellationToken ct)
        => Ok(await shares.ReadAsync(threadId, ct));

    [HttpGet("images/{imageId}")]
    public async Task<IActionResult> Image(string threadId, string imageId, CancellationToken ct)
        => File(await shares.ImageAsync(threadId, imageId, ct), "image/png");
}