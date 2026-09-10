using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MnaiWork.Api.Agent;
using MnaiWork.Api.Agent.Skills;
using MnaiWork.Api.Agent.Tools;
using MnaiWork.Api.Configuration;
using MnaiWork.Api.Data;
using MnaiWork.Api.Models;
using Xunit;

namespace MnaiWork.Api.Tests;

public sealed class AgentDeploymentExecutionTests
{
    [Theory]
    [InlineData("success", true, RunStatus.Completed)]
    [InlineData("failure", true, RunStatus.Failed)]
    [InlineData("exception", true, RunStatus.Failed)]
    [InlineData("success", false, RunStatus.Failed)]
    public async Task ExactDeploy_BypassesModelAndReportsOnlyActualToolOutcome(
        string outcome, bool hasPreview, RunStatus expectedStatus)
    {
        var arguments = JsonSerializer.SerializeToElement(new
        {
            projectSlug = "demo-one",
            backendPackageFileId = "tested-backend",
            frontendPackageFileId = "tested-frontend",
            cosmosDatabaseName = "app",
            cosmosContainerName = "items",
            healthCheckPath = "/health"
        });
        var messages = new MemoryMessages();
        if (hasPreview)
        {
            messages.Items.Add(new ChatMessage
            {
                Role = MessageRole.Tool, Sequence = 1, ToolName = "preview_azure_project",
                ToolArguments = arguments, ToolSucceeded = true, Content = "preview succeeded"
            });
        }
        messages.Items.Add(new ChatMessage
        {
            Role = MessageRole.Assistant, Sequence = 2, Content = "Unsupported historical success claim"
        });
        messages.Items.Add(new ChatMessage { Role = MessageRole.User, Sequence = 3, Content = "DEPLOY demo-one" });
        var runs = new MemoryRuns();
        var tool = new DeploymentTool(outcome);
        var bus = new AgentEventBus();
        using var subscription = bus.Subscribe("run-1");
        var skills = new AgentSkillRegistry(new[] { new SoftwareFactorySkill() });
        var runner = new AgentRunner(null!, null!, messages, runs, bus,
            new ToolRegistry(new[] { tool }), skills, Options.Create(new AzureOpenAiOptions()),
            NullLogger<AgentRunner>.Instance);

        await runner.RunAsync(new AgentRunRequest("run-1", "thread-1", "user-1"), CancellationToken.None);

        Assert.Equal(expectedStatus, runs.Run.Status);
        Assert.Equal(hasPreview ? 1 : 0, tool.Calls);
        Assert.False(skills.IsToolAvailable("run-1", "deploy_azure_project"));
        var replies = messages.Items.Where(message => message.RunId == "run-1"
            && message.Role == MessageRole.Assistant).ToList();
        if (hasPreview)
        {
            Assert.Equal(arguments.GetRawText(), tool.Arguments!.Value.GetRawText());
            if (outcome == "exception")
            {
                Assert.Empty(replies);
                Assert.Equal("deployment exception", runs.Run.Error);
            }
            else
            {
                Assert.Equal(tool.Result.Output, Assert.Single(replies).Content);
                var evidence = Assert.Single(messages.Items, message => message.ToolName == "deploy_azure_project");
                Assert.Equal(tool.Result.Success, evidence.ToolSucceeded);
                Assert.Equal(arguments.GetRawText(), evidence.ToolArguments!.Value.GetRawText());
            }
        }
        else
        {
            Assert.Contains("Deployment was not started", Assert.Single(replies).Content);
        }
        var events = new List<AgentEvent>();
        await foreach (var entry in subscription.Reader.ReadAllAsync())
        {
            events.Add(entry);
        }
        Assert.DoesNotContain(events, entry => entry.Type == "delta");
        Assert.Equal(hasPreview ? 1 : 0, events.Count(entry => entry.Type == "tool" && entry.ToolStatus == "started"));
        Assert.Contains(events, entry => entry.Type == "done");
    }

    private sealed class DeploymentTool(string outcome) : IAgentTool
    {
        public string Name => "deploy_azure_project";
        public string Description => "Test deployment";
        public string ParametersSchema => "{}";
        public int Calls { get; private set; }
        public JsonElement? Arguments { get; private set; }
        public ToolResult Result => outcome == "success"
            ? ToolResult.Ok("Verified deployment output from tool")
            : ToolResult.Fail("Deployment rejected by tool");

        public Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context, CancellationToken ct)
        {
            Calls++;
            Arguments = arguments.Clone();
            if (outcome == "exception")
            {
                throw new InvalidOperationException("deployment exception");
            }
            return Task.FromResult(Result);
        }
    }

    private sealed class MemoryMessages : IMessageRepository
    {
        public List<ChatMessage> Items { get; } = new();
        public Task<IReadOnlyList<ChatMessage>> ListAsync(string threadId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ChatMessage>>(Items.ToArray());
        public Task<ChatMessage> AddAsync(ChatMessage message, CancellationToken ct = default)
        {
            Items.Add(message);
            return Task.FromResult(message);
        }
        public Task<ChatMessage> UpsertAsync(ChatMessage message, CancellationToken ct = default)
            => Task.FromResult(message);
        public Task<ChatMessage?> GetAsync(string threadId, string messageId, CancellationToken ct = default)
            => Task.FromResult(Items.FirstOrDefault(message => message.Id == messageId));
        public Task<long> GetNextSequenceAsync(string threadId, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task DeleteByThreadAsync(string threadId, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private sealed class MemoryRuns : IRunRepository
    {
        public AgentRun Run { get; private set; } = new() { Id = "run-1", ThreadId = "thread-1" };
        public Task<AgentRun?> GetAsync(string threadId, string runId, CancellationToken ct = default)
            => Task.FromResult<AgentRun?>(Run);
        public Task<AgentRun> UpsertAsync(AgentRun run, CancellationToken ct = default)
        {
            Run = run;
            return Task.FromResult(run);
        }
        public Task<AgentRun> CreateAsync(AgentRun run, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task DeleteByThreadAsync(string threadId, CancellationToken ct = default)
            => throw new NotSupportedException();
    }
}