using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MnaiWork.Api.Agent;
using MnaiWork.Api.Agent.Skills;
using MnaiWork.Api.Agent.Tools;
using MnaiWork.Api.Configuration;
using MnaiWork.Api.Data;
using MnaiWork.Api.Models;
using MnaiWork.Api.Storage;
using OpenAI.Responses;
using Xunit;
using MessageRole = MnaiWork.Api.Models.MessageRole;

#pragma warning disable OPENAI001

namespace MnaiWork.Api.Tests;

public sealed class BuildScreenshotInputTests
{
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a6WQAAAAASUVORK5CYII=");

    [Theory]
    [InlineData(false, false, 0)]
    [InlineData(true, false, 0)]
    [InlineData(false, true, 0)]
    [InlineData(false, false, 650_000)]
    [InlineData(true, false, 650_000)]
    [InlineData(false, false, 5 * 1024 * 1024)]
    public async Task Runner_SendsActualImagesAfterBuildAndOnContinuation(bool continuation, bool updateSource, int imageSize)
    {
        var imageBytes = imageSize == 0 ? Png : new byte[imageSize];
        if (imageSize > 0) Png.CopyTo(imageBytes, 0);
        var artifacts = Images();
        var messages = new Messages();
        messages.Items.Add(new ChatMessage { Role = MessageRole.Tool, Sequence = 1, Artifacts = new()
        { new Artifact { Id = "source", Kind = ArtifactKind.SourceZip, FileName = "demo-source.zip" } } });
        if (continuation) messages.Items.Add(new ChatMessage
        {
            Role = MessageRole.Tool, Sequence = 2, ToolName = "build_test_project", ToolSucceeded = true,
            ToolArguments = JsonSerializer.SerializeToElement(new { projectSlug = "demo", sourceArchiveFileId = "source" }),
            Artifacts = artifacts.ToList()
        });
        messages.Items.Add(new ChatMessage { Role = MessageRole.User, Sequence = 3, Content = "继续" });
        var handler = new ModelHandler(!continuation, updateSource);
        using var http = new HttpClient(handler);
        var client = new ResponsesClient(new ApiKeyCredential("test-only"), new ResponsesClientOptions
        { Endpoint = new Uri("https://model.test/v1"), Transport = new HttpClientPipelineTransport(http) });
        var options = Options.Create(new AzureOpenAiOptions { Deployment = "test", MaxToolIterations = 3 });
        var runs = new Runs();
        var skills = new AgentSkillRegistry(new[] { new SoftwareFactorySkill() });
        skills.TryLoad("run", "software-factory", out _);
        var storage = new Storage(imageBytes);
        var runner = new AgentRunner(client, new ContextManager(client, options, NullLogger<ContextManager>.Instance),
            messages, runs, new AgentEventBus(), new ToolRegistry(new IAgentTool[] { new BuildTool(artifacts), new UpdateTool() }), skills,
            options, NullLogger<AgentRunner>.Instance, storage);
        await runner.RunAsync(new AgentRunRequest("run", "thread", "user"), CancellationToken.None);
        Assert.Equal(updateSource ? RunStatus.Failed : RunStatus.Completed, runs.Run.Status);
        Assert.Equal(continuation ? 1 : updateSource ? 3 : 2, handler.Requests.Count);
        var imageRequest = handler.Requests[continuation ? 0 : 1];
        using var request = JsonDocument.Parse(imageRequest);
        var contents = request.RootElement.GetProperty("input").EnumerateArray()
            .Where(item => item.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
            .SelectMany(item => item.GetProperty("content").EnumerateArray()).ToArray();
        var images = contents.Where(part => part.GetProperty("type").GetString() == "input_image").ToArray();
        Assert.Equal(2, images.Length);
        Assert.All(images, image => Assert.Equal("data:image/png;base64," + Convert.ToBase64String(imageBytes), image.GetProperty("image_url").GetString()));
        Assert.Contains("SERVER VISUAL REVIEW", imageRequest);
        Assert.Equal(2, storage.ReadCount);
        if (updateSource) Assert.DoesNotContain("data:image/png;base64,", handler.Requests.Last());
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("invalid")]
    [InlineData("foreign")]
    [InlineData("oversize")]
    public async Task RejectsUnavailableOrUntrustedImages(string scenario)
    {
        var artifacts = Images();
        var messages = new Messages();
        if (scenario != "foreign") messages.Items.Add(new ChatMessage { Artifacts = artifacts.ToList() });
        var bytes = scenario switch { "missing" => null, "invalid" => new byte[24], "oversize" => new byte[5 * 1024 * 1024 + 1], _ => Png };
        var storage = new Storage(bytes);
        await Assert.ThrowsAsync<InvalidOperationException>(() => BuildScreenshotInput.CreateAsync(artifacts, "thread", messages, storage, CancellationToken.None));
        if (scenario == "foreign") Assert.Equal(0, storage.ReadCount);
    }

    private static Artifact[] Images() => new[]
    {
        new Artifact { Id = "desktop", Kind = ArtifactKind.UiScreenshot, FileName = "demo-ui-desktop.png", BlobPath = "user/thread/desktop" },
        new Artifact { Id = "mobile", Kind = ArtifactKind.UiScreenshot, FileName = "demo-ui-mobile.png", BlobPath = "user/thread/mobile" },
        new Artifact { Id = "backend", Kind = ArtifactKind.BackendPackage, FileName = "demo-backend.zip" },
        new Artifact { Id = "frontend", Kind = ArtifactKind.FrontendPackage, FileName = "demo-frontend.zip" }
    };

    private sealed class BuildTool(Artifact[] artifacts) : IAgentTool
    {
        public string Name => "build_test_project";
        public string Description => "Build fixture";
        public string ParametersSchema => "{}";
        public Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context, CancellationToken ct)
            => Task.FromResult(ToolResult.Ok("Build passed", artifacts));
    }

    private sealed class UpdateTool : IAgentTool
    {
        public string Name => "update_project_workspace";
        public string Description => "Update source fixture";
        public string ParametersSchema => "{}";
        public Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context, CancellationToken ct)
            => Task.FromResult(ToolResult.Ok("Source updated", new[]
            { new Artifact { Id = "new-source", Kind = ArtifactKind.SourceZip, FileName = "demo-source.zip" } }));
    }

    private sealed class ModelHandler(bool callBuild, bool updateSource) : HttpMessageHandler
    {
        public List<string> Requests { get; } = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(await request.Content!.ReadAsStringAsync(ct));
            var build = callBuild && Requests.Count == 1;
            var toolCall = build || updateSource && Requests.Count == 2;
            var data = toolCall ? JsonSerializer.Serialize(new
            {
                type = "response.completed", sequence_number = 1, response = new
                {
                    id = $"response-{Requests.Count}", @object = "response", created_at = 0, status = "completed", model = "test",
                    output = new[] { new { type = "function_call", id = $"item-{Requests.Count}", call_id = $"call-{Requests.Count}", name = build ? "build_test_project" : "update_project_workspace",
                        arguments = "{\"projectSlug\":\"demo\",\"sourceArchiveFileId\":\"source\"}", status = "completed" } }
                }
            }) : JsonSerializer.Serialize(new { type = "response.output_text.delta", sequence_number = 1,
                item_id = "message", output_index = 0, content_index = 0, delta = "Visual review received", logprobs = Array.Empty<object>() });
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(
                $"event: {(toolCall ? "response.completed" : "response.output_text.delta")}\ndata: {data}\n\ndata: [DONE]\n\n", Encoding.UTF8, "text/event-stream") };
        }
    }

    private sealed class Storage(byte[]? bytes) : IFileStorage
    {
        public int ReadCount { get; private set; }
        public Task<byte[]?> ReadBytesAsync(string blobPath, CancellationToken ct = default) { ReadCount++; return Task.FromResult(bytes); }
        public Task<Artifact> UploadAsync(ArtifactOwner owner, string fileName, ArtifactKind kind, byte[] content, string contentType, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Attachment> UploadUserFileAsync(ArtifactOwner owner, string fileName, AttachmentKind kind, byte[] content, string contentType, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<(Stream Stream, string ContentType, string FileName)?> OpenReadAsync(string blobPath, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string> GetDownloadUrlAsync(string blobPath, string fileName, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteThreadFilesAsync(ArtifactOwner owner, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class Messages : IMessageRepository
    {
        public List<ChatMessage> Items { get; } = new();
        public Task<IReadOnlyList<ChatMessage>> ListAsync(string threadId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ChatMessage>>(Items.ToArray());
        public Task<ChatMessage> AddAsync(ChatMessage message, CancellationToken ct = default) { Items.Add(message); return Task.FromResult(message); }
        public Task<ChatMessage> UpsertAsync(ChatMessage message, CancellationToken ct = default) => Task.FromResult(message);
        public Task<ChatMessage?> GetAsync(string threadId, string messageId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<long> GetNextSequenceAsync(string threadId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteByThreadAsync(string threadId, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class Runs : IRunRepository
    {
        public AgentRun Run { get; private set; } = new() { Id = "run", ThreadId = "thread" };
        public Task<AgentRun?> GetAsync(string threadId, string runId, CancellationToken ct = default) => Task.FromResult<AgentRun?>(Run);
        public Task<AgentRun> UpsertAsync(AgentRun run, CancellationToken ct = default) { Run = run; return Task.FromResult(run); }
        public Task<AgentRun> CreateAsync(AgentRun run, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteByThreadAsync(string threadId, CancellationToken ct = default) => throw new NotSupportedException();
    }
}