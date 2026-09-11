using System.Text.Json;
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using OpenAI.Responses;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MnaiWork.Api.Agent;
using MnaiWork.Api.Agent.Skills;
using MnaiWork.Api.Agent.Tools;
using MnaiWork.Api.Configuration;
using MnaiWork.Api.Data;
using MnaiWork.Api.Models;
using Xunit;
using MessageRole = MnaiWork.Api.Models.MessageRole;

#pragma warning disable OPENAI001

namespace MnaiWork.Api.Tests;

public sealed class AgentDeploymentExecutionTests
{
    [Theory]
    [InlineData("继续", true, false)]
    [InlineData("APPROVE UI", true, false)]
    [InlineData("Explain dependency injection", false, false)]
    [InlineData("继续", false, true)]
    [InlineData("APPROVE UI", false, true)]
    public async Task BuildContinuation_DoesNotPublishUnsupportedStreamingSuccess(string userText, bool blocked, bool buildPassed)
    {
        const string modelText = "Build passed; screenshots generated; APPROVE UI";
        var messages = new MemoryMessages();
        messages.Items.Add(new ChatMessage { Role = MessageRole.Tool, Sequence = 1, Artifacts = new()
        {
            new Artifact { Id = "source-1", Kind = ArtifactKind.SourceZip, FileName = "textblog-source.zip" }
        } });
        messages.Items.Add(new ChatMessage
        {
            Role = MessageRole.Tool, Sequence = 2, ToolName = "build_test_project", ToolSucceeded = false,
            Content = "CS0246 IConfiguration",
            Artifacts = new() { new Artifact { Id = "report-1", Kind = ArtifactKind.BuildReport, FileName = "textblog-build-report.txt" } }
        });
        messages.Items.Add(new ChatMessage { Role = MessageRole.User, Sequence = 3, Content = userText });
        if (buildPassed)
        {
            messages.Items[1].ToolSucceeded = true;
            messages.Items[1].Content = "Build passed";
            messages.Items[1].ToolArguments = JsonSerializer.SerializeToElement(new { projectSlug = "textblog", sourceArchiveFileId = "source-1" });
            messages.Items[1].Artifacts.AddRange(new[]
            {
                new Artifact { Id = "backend", Kind = ArtifactKind.BackendPackage, FileName = "textblog-backend.zip" },
                new Artifact { Id = "frontend", Kind = ArtifactKind.FrontendPackage, FileName = "textblog-frontend.zip" },
                new Artifact { Id = "desktop", Kind = ArtifactKind.UiScreenshot, FileName = "textblog-ui-desktop.png" },
                new Artifact { Id = "mobile", Kind = ArtifactKind.UiScreenshot, FileName = "textblog-ui-mobile.png" }
            });
        }
        var responseText = blocked || buildPassed ? modelText : "Dependency injection explanation";
        var handler = new ModelStreamHandler(responseText);
        using var http = new HttpClient(handler);
        var client = new ResponsesClient(new ApiKeyCredential("test-only"), new ResponsesClientOptions
        {
            Endpoint = new Uri("https://model.test/v1"), Transport = new HttpClientPipelineTransport(http)
        });
        var options = Options.Create(new AzureOpenAiOptions { Deployment = "test-model", MaxToolIterations = 2 });
        var runs = new MemoryRuns();
        var bus = new AgentEventBus();
        using var subscription = bus.Subscribe("run-1");
        var runner = new AgentRunner(client, new ContextManager(client, options, NullLogger<ContextManager>.Instance),
            messages, runs, bus, new ToolRegistry(Array.Empty<IAgentTool>()),
            new AgentSkillRegistry(new[] { new SoftwareFactorySkill() }), options, NullLogger<AgentRunner>.Instance);

        await runner.RunAsync(new AgentRunRequest("run-1", "thread-1", "user-1"), CancellationToken.None);

        var replies = messages.Items.Where(message => message.RunId == "run-1" && message.Role == MessageRole.Assistant).ToArray();
        Assert.NotEmpty(replies);
        var events = new List<AgentEvent>();
        await foreach (var entry in subscription.Reader.ReadAllAsync()) events.Add(entry);
        Assert.DoesNotContain(events, entry => entry.Type == "delta");
        if (blocked)
        {
            Assert.Equal(RunStatus.Failed, runs.Run.Status);
            Assert.Contains("CS0246", Assert.Single(replies, reply => !string.IsNullOrWhiteSpace(reply.Content)).Content);
            Assert.DoesNotContain(events, entry => entry.Content?.Contains(modelText, StringComparison.Ordinal) == true);
            Assert.Equal(userText == "继续" ? 2 : 1, handler.Calls);
        }
        else
        {
            Assert.Equal(RunStatus.Completed, runs.Run.Status);
            Assert.Equal(responseText, Assert.Single(replies).Content);
        }
        if (!buildPassed) Assert.Contains("SERVER BUILD EVIDENCE", handler.LastRequest);
    }

    [Fact]
    public async Task BuildToolRounds_ExecuteWithoutRepeatedChatWarnings()
    {
        var messages = new MemoryMessages();
        messages.Items.Add(new ChatMessage { Role = MessageRole.Tool, Sequence = 1, Artifacts = new()
        {
            new Artifact { Id = "source-1", Kind = ArtifactKind.SourceZip, FileName = "textblog-source.zip" }
        } });
        messages.Items.Add(new ChatMessage { Role = MessageRole.User, Sequence = 2, Content = "继续" });
        var handler = new ModelStreamHandler("Build passed; screenshots generated", toolRounds: 3);
        using var http = new HttpClient(handler);
        var client = new ResponsesClient(new ApiKeyCredential("test-only"), new ResponsesClientOptions
        {
            Endpoint = new Uri("https://model.test/v1"), Transport = new HttpClientPipelineTransport(http)
        });
        var options = Options.Create(new AzureOpenAiOptions { Deployment = "test-model", MaxToolIterations = 4 });
        var runs = new MemoryRuns();
        var bus = new AgentEventBus();
        using var subscription = bus.Subscribe("run-1");
        var skills = new AgentSkillRegistry(new[] { new SoftwareFactorySkill() });
        skills.TryLoad("run-1", "software-factory", out _);
        var tool = new WorkspaceReadTool();
        var runner = new AgentRunner(client, new ContextManager(client, options, NullLogger<ContextManager>.Instance),
            messages, runs, bus, new ToolRegistry(new[] { tool }), skills, options, NullLogger<AgentRunner>.Instance);

        await runner.RunAsync(new AgentRunRequest("run-1", "thread-1", "user-1"), CancellationToken.None);

        Assert.Equal(3, tool.Calls);
        Assert.Equal(4, handler.Calls);
        Assert.Equal(RunStatus.Failed, runs.Run.Status);
        var replies = messages.Items.Where(message => message.RunId == "run-1" && message.Role == MessageRole.Assistant).ToArray();
        Assert.Equal(4, replies.Length);
        Assert.All(replies.Take(3), reply => Assert.True(string.IsNullOrEmpty(reply.Content)));
        Assert.Contains("build_test_project", replies.Last().Content);
        var events = new List<AgentEvent>();
        await foreach (var entry in subscription.Reader.ReadAllAsync()) events.Add(entry);
        Assert.Equal(3, events.Count(entry => entry.Type == "tool" && entry.ToolStatus == "completed"));
        Assert.Single(events, entry => entry.Type == "message_done" && !string.IsNullOrWhiteSpace(entry.Content));
        Assert.DoesNotContain(events, entry => entry.Type == "delta");
        using var request = JsonDocument.Parse(handler.LastRequest);
        Assert.Equal(3, request.RootElement.GetProperty("input").EnumerateArray()
            .Count(item => item.GetProperty("type").GetString() == "function_call_output"));
        Assert.DoesNotContain("screenshots generated", handler.LastRequest);
    }

    private sealed class WorkspaceReadTool : IAgentTool
    {
        public string Name => "read_project_workspace";
        public string Description => "Read test workspace";
        public string ParametersSchema => "{}";
        public int Calls { get; private set; }
        public Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(ToolResult.Ok("Workspace source inspected"));
        }
    }

    private sealed class ModelStreamHandler(string text, int toolRounds = 0) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string LastRequest { get; private set; } = "";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            LastRequest = await request.Content!.ReadAsStringAsync(ct);
            var delta = JsonSerializer.Serialize(new
            {
                type = "response.output_text.delta", sequence_number = 1, item_id = "message-1",
                output_index = 0, content_index = 0, delta = text, logprobs = Array.Empty<object>()
            });
            var completion = "";
            if (Calls <= toolRounds)
            {
                var completed = JsonSerializer.Serialize(new
                {
                    type = "response.completed", sequence_number = 2,
                    response = new
                    {
                        id = $"response-{Calls}", @object = "response", created_at = 0, status = "completed",
                        model = "test-model", output = new[]
                        {
                            new { type = "function_call", id = $"item-{Calls}", call_id = $"call-{Calls}",
                                name = "read_project_workspace", arguments = "{}", status = "completed" }
                        }
                    }
                });
                completion = $"event: response.completed\ndata: {completed}\n\n";
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($"event: response.output_text.delta\ndata: {delta}\n\n{completion}data: [DONE]\n\n",
                    Encoding.UTF8, "text/event-stream")
            };
        }
    }

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