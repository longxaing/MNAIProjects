using System.Text.Json;
using MnaiWork.Api.Agent;
using MnaiWork.Api.Data;
using MnaiWork.Api.Infrastructure;
using MnaiWork.Api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;

namespace MnaiWork.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/threads/{threadId}/runs")]
public sealed class RunsController : ControllerBase
{
    private readonly IRunRepository _runs;
    private readonly IAgentEventBus _bus;
    private readonly ICurrentUser _me;

    public RunsController(IRunRepository runs, IAgentEventBus bus, ICurrentUser me)
    {
        _runs = runs;
        _bus = bus;
        _me = me;
    }

    /// <summary>Server-sent events for a run. Live text deltas, tool activity and artifacts.</summary>
    [HttpGet("{runId}/stream")]
    public async Task Stream(string threadId, string runId, CancellationToken ct)
    {
        var run = await _runs.GetAsync(threadId, runId, ct);
        if (run is null || run.UserId != _me.Id)
        {
            Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        Response.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache, no-transform";
        Response.Headers["X-Accel-Buffering"] = "no";
        HttpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        // Subscribe before checking terminal state so we never miss events for an active run.
        using var subscription = _bus.Subscribe(runId);

        await WriteAsync(new AgentEvent
        {
            Type = "run",
            RunId = runId,
            Status = run.Status.ToString().ToLowerInvariant()
        }, ct);

        var terminal = run.Status is RunStatus.Completed or RunStatus.Failed or RunStatus.Canceled;
        if (terminal && !_bus.IsActive(runId))
        {
            await WriteAsync(new AgentEvent { Type = "done", RunId = runId }, ct);
            return;
        }

        try
        {
            await foreach (var evt in subscription.Reader.ReadAllAsync(ct))
            {
                await WriteAsync(evt, ct);
                if (evt.Type == "done")
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Client disconnected — nothing to do.
        }
    }

    private async Task WriteAsync(AgentEvent evt, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(evt, JsonDefaults.Options);
        await Response.WriteAsync($"data: {json}\n\n", ct);
        await Response.Body.FlushAsync(ct);
    }
}
