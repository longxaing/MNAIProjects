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

    internal TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(15);

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
        run = await _runs.GetAsync(threadId, runId, ct);
        if (run is null || run.UserId != _me.Id)
        {
            Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        await WriteAsync(new AgentEvent
        {
            Type = "run",
            RunId = runId,
            Status = run.Status.ToString().ToLowerInvariant(),
            Error = run.Error
        }, ct);

        if (await WriteTerminalAsync(run, ct)) return;

        try
        {
            using var waiting = CancellationTokenSource.CreateLinkedTokenSource(ct);
            using var timer = new PeriodicTimer(HeartbeatInterval);
            var available = subscription.Reader.WaitToReadAsync(waiting.Token).AsTask();
            var heartbeat = timer.WaitForNextTickAsync(waiting.Token).AsTask();
            try
            {
                while (true)
                {
                    await Task.WhenAny(available, heartbeat);
                    if (heartbeat.IsCompleted)
                    {
                        if (!await heartbeat) return;
                        await Response.WriteAsync(": keep-alive\n\n", ct);
                        await Response.Body.FlushAsync(ct);
                        var current = await _runs.GetAsync(threadId, runId, ct);
                        if (current is null || current.UserId != _me.Id)
                        {
                            await WriteAsync(new AgentEvent { Type = "error", RunId = runId, Error = "Run is no longer available." }, ct);
                            await WriteAsync(new AgentEvent { Type = "done", RunId = runId }, ct);
                            return;
                        }
                        if (await WriteTerminalAsync(current, ct)) return;
                        heartbeat = timer.WaitForNextTickAsync(waiting.Token).AsTask();
                    }
                    if (available.IsCompleted)
                    {
                        if (!await available) return;
                        while (subscription.Reader.TryRead(out var evt))
                        {
                            await WriteAsync(evt, ct);
                            if (evt.Type == "done") return;
                        }
                        available = subscription.Reader.WaitToReadAsync(waiting.Token).AsTask();
                    }
                }
            }
            finally
            {
                waiting.Cancel();
                try { await Task.WhenAll(available, heartbeat); }
                catch (OperationCanceledException) { }
            }
        }
        catch (OperationCanceledException)
        {
            // Client disconnected — nothing to do.
        }
    }

    private async Task<bool> WriteTerminalAsync(AgentRun run, CancellationToken ct)
    {
        if (run.Status is not (RunStatus.Completed or RunStatus.Failed or RunStatus.Canceled)) return false;
        await WriteAsync(new AgentEvent
        {
            Type = "run", RunId = run.Id, Status = run.Status.ToString().ToLowerInvariant(), Error = run.Error
        }, ct);
        if (run.Status == RunStatus.Failed)
        {
            await WriteAsync(new AgentEvent
            {
                Type = "error", RunId = run.Id, Error = run.Error ?? "The agent run failed."
            }, ct);
        }
        await WriteAsync(new AgentEvent { Type = "done", RunId = run.Id }, ct);
        return true;
    }

    private async Task WriteAsync(AgentEvent evt, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(evt, JsonDefaults.Options);
        await Response.WriteAsync($"data: {json}\n\n", ct);
        await Response.Body.FlushAsync(ct);
    }
}
