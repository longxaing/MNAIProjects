using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using MnaiWork.Api.Agent;
using MnaiWork.Api.Controllers;
using MnaiWork.Api.Data;
using MnaiWork.Api.Infrastructure;
using MnaiWork.Api.Models;
using Xunit;

namespace MnaiWork.Api.Tests;

public sealed class RunsControllerTests
{
    [Theory]
    [InlineData(RunStatus.Completed, false)]
    [InlineData(RunStatus.Failed, false)]
    [InlineData(RunStatus.Canceled, false)]
    [InlineData(RunStatus.Completed, true)]
    [InlineData(RunStatus.Failed, true)]
    public async Task Stream_TerminalRunWithoutLiveEvents_ClosesWithDone(
        RunStatus status, bool finishBetweenReads)
    {
        var bus = new AgentEventBus();
        bus.Complete("run-1");
        var repository = new RunRepository(status, finishBetweenReads);
        var context = new DefaultHttpContext();
        context.User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("oid", "user-1") }));
        using var output = new MemoryStream();
        context.Response.Body = output;
        var controller = new RunsController(repository, bus,
            new CurrentUser(new HttpContextAccessor { HttpContext = context }))
        {
            ControllerContext = new ControllerContext { HttpContext = context }
        };
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        await controller.Stream("thread-1", "run-1", cancellation.Token);

        Assert.False(cancellation.IsCancellationRequested, "Terminal stream waited for events that were already lost.");
        var text = Encoding.UTF8.GetString(output.ToArray());
        Assert.Contains("\"type\":\"done\"", text);
        Assert.Contains($"\"status\":\"{status.ToString().ToLowerInvariant()}\"", text);
        if (status == RunStatus.Failed)
        {
            Assert.Contains("actual deployment failure", text);
        }
    }

    [Fact]
    public async Task Stream_ActiveRun_DeliversLiveEventsBeforeClosing()
    {
        var bus = new AgentEventBus();
        var repository = new RunRepository(RunStatus.Running, false, () =>
        {
            bus.Publish("run-1", new AgentEvent { Type = "tool", Tool = "deploy_azure_project", ToolStatus = "started" });
            bus.Publish("run-1", new AgentEvent { Type = "message_done", Content = "Actual deployment result" });
            bus.Publish("run-1", new AgentEvent { Type = "done" });
            bus.Complete("run-1");
        });
        var context = new DefaultHttpContext();
        context.User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("oid", "user-1") }));
        using var output = new MemoryStream();
        context.Response.Body = output;
        var controller = new RunsController(repository, bus,
            new CurrentUser(new HttpContextAccessor { HttpContext = context }))
        {
            ControllerContext = new ControllerContext { HttpContext = context }
        };
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        await controller.Stream("thread-1", "run-1", cancellation.Token);

        Assert.False(cancellation.IsCancellationRequested);
        var text = Encoding.UTF8.GetString(output.ToArray());
        Assert.Contains("deploy_azure_project", text);
        Assert.Contains("Actual deployment result", text);
        Assert.Contains("\"type\":\"done\"", text);
    }

    [Theory]
    [InlineData(RunStatus.Completed)]
    [InlineData(RunStatus.Failed)]
    [InlineData(RunStatus.Canceled)]
    public async Task Stream_SilentRun_SendsHeartbeatsAndRecoversPersistedTerminalState(RunStatus status)
    {
        var repository = new RunRepository(status, false, terminalRead: 4);
        var context = new DefaultHttpContext();
        context.User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("oid", "user-1") }));
        using var output = new MemoryStream();
        context.Response.Body = output;
        var controller = new RunsController(repository, new AgentEventBus(),
            new CurrentUser(new HttpContextAccessor { HttpContext = context }))
        {
            ControllerContext = new ControllerContext { HttpContext = context },
            HeartbeatInterval = TimeSpan.FromMilliseconds(10)
        };
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        await controller.Stream("thread-1", "run-1", cancellation.Token);

        Assert.False(cancellation.IsCancellationRequested);
        var text = Encoding.UTF8.GetString(output.ToArray());
        Assert.True(text.Split(": keep-alive").Length >= 3);
        Assert.Contains($"\"status\":\"{status.ToString().ToLowerInvariant()}\"", text);
        Assert.Contains("\"type\":\"done\"", text);
        if (status == RunStatus.Failed) Assert.Contains("actual deployment failure", text);
    }

    [Fact]
    public async Task Stream_DisconnectedSilentRun_StopsWaitingWithoutReportingSuccess()
    {
        var context = new DefaultHttpContext();
        context.User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("oid", "user-1") }));
        using var output = new MemoryStream();
        context.Response.Body = output;
        using var cancellation = new CancellationTokenSource();
        var controller = new RunsController(new RunRepository(RunStatus.Running, false), new AgentEventBus(),
            new CurrentUser(new HttpContextAccessor { HttpContext = context }))
        {
            ControllerContext = new ControllerContext { HttpContext = context }
        };

        var streaming = controller.Stream("thread-1", "run-1", cancellation.Token);
        Assert.False(streaming.IsCompleted);
        cancellation.Cancel();
        await streaming.WaitAsync(TimeSpan.FromSeconds(2));

        var text = Encoding.UTF8.GetString(output.ToArray());
        Assert.Contains("\"status\":\"running\"", text);
        Assert.DoesNotContain("\"status\":\"completed\"", text);
        Assert.DoesNotContain("\"type\":\"done\"", text);
    }

    private sealed class RunRepository(RunStatus status, bool finishBetweenReads, Action? afterSubscribe = null,
        int terminalRead = 0) : IRunRepository
    {
        private int _reads;
        public Task<AgentRun?> GetAsync(string threadId, string runId, CancellationToken ct = default)
        {
            _reads++;
            if (_reads == 2)
            {
                afterSubscribe?.Invoke();
            }
            return Task.FromResult<AgentRun?>(new AgentRun
            {
                Id = runId,
                ThreadId = threadId,
                UserId = "user-1",
                Status = (finishBetweenReads && _reads == 1) || _reads < terminalRead ? RunStatus.Running : status,
                Error = status == RunStatus.Failed ? "actual deployment failure" : null
            });
        }
        public Task<AgentRun> CreateAsync(AgentRun run, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<AgentRun> UpsertAsync(AgentRun run, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task DeleteByThreadAsync(string threadId, CancellationToken ct = default)
            => throw new NotSupportedException();
    }
}