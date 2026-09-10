using System.Net;
using System.Text;
using System.Text.Json;
using MnaiWork.BuildExecution;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MnaiWork.Api.Tests;

public sealed class E2BBuildExecutionTests
{
    [Fact]
    public async Task BuildExecutor_DelegatesConfiguredBuildAndDisposesSandbox()
    {
        var configuration = BuildConfiguration();
        var expected = SuccessfulResponse();
        var client = new FakeSandboxClient(expected);
        var executor = new BuildExecutor(
            configuration,
            client,
            NullLogger<BuildExecutor>.Instance);

        var result = await executor.ExecuteAsync(
            new byte[] { 1, 2, 3 },
            "GeneratedApp.sln",
            "src/backend/GeneratedApp.Api.csproj",
            "src/frontend",
            CancellationToken.None);

        Assert.Same(expected, result);
        Assert.Equal("mnaiwork-software-factory", client.TemplateId);
        Assert.Equal(TimeSpan.FromMinutes(17), client.Timeout);
        Assert.NotNull(client.Session.Request);
        Assert.Equal(6, client.Session.Request!.CommandTimeoutMinutes);
        Assert.Equal(17, client.Session.Request.TotalTimeoutMinutes);
        Assert.Equal("1.62.1", client.Session.Request.PlaywrightVersion);
        Assert.True(client.Session.Disposed);
    }

    [Fact]
    public async Task BuildExecutor_ReusesSandboxForSameIsolatedProjectScope()
    {
        var client = new FakeSandboxClient(SuccessfulResponse());
        await using var executor = new BuildExecutor(
            BuildConfiguration(),
            client,
            NullLogger<BuildExecutor>.Instance);

        await executor.ExecuteAsync(
            new byte[] { 1 },
            "GeneratedApp.sln",
            "src/backend/GeneratedApp.Api.csproj",
            "src/frontend",
            CancellationToken.None,
            "user:thread:project");
        await executor.ExecuteAsync(
            new byte[] { 2 },
            "GeneratedApp.sln",
            "src/backend/GeneratedApp.Api.csproj",
            "src/frontend",
            CancellationToken.None,
            "user:thread:project");

        Assert.Equal(1, client.CreateCount);
        Assert.Equal(TimeSpan.FromMinutes(17), client.Timeout);
        Assert.Equal(2, client.Session.BuildCount);
        Assert.False(client.Session.Disposed);
    }

    [Fact]
    public async Task BuildExecutor_PrioritizesPreviousFailureWithoutSharingAcrossProjects()
    {
        var failed = new BuildProjectResponse(
            false,
            "frontend tests failed",
            new[] { new BuildStepResult("frontend tests", false, 1, 10, "failed") },
            null,
            null);
        var client = new FakeSandboxClient(failed);
        await using var executor = new BuildExecutor(
            BuildConfiguration(),
            client,
            NullLogger<BuildExecutor>.Instance);

        await executor.ExecuteAsync(
            new byte[] { 1 }, "app.sln", "api.csproj", "frontend",
            CancellationToken.None, "user:thread:project-one");
        client.Session.Response = SuccessfulResponse();
        await executor.ExecuteAsync(
            new byte[] { 2 }, "app.sln", "api.csproj", "frontend",
            CancellationToken.None, "user:thread:project-one");
        await executor.ExecuteAsync(
            new byte[] { 3 }, "app.sln", "api.csproj", "frontend",
            CancellationToken.None, "user:thread:project-two");

        Assert.Equal("frontend tests", client.Session.Requests[1].PreferredFirstStage);
        Assert.Equal(2, client.CreateCount);
        Assert.Equal(2, client.Sessions.Count);
    }

    [Fact]
    public async Task BuildExecutor_RequiresTemplateIdFromKeyVaultConfiguration()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BuildExecution:Enabled"] = "true"
            })
            .Build();
        var client = new FakeSandboxClient(SuccessfulResponse());
        var executor = new BuildExecutor(
            configuration,
            client,
            NullLogger<BuildExecutor>.Instance);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => executor.ExecuteAsync(
            new byte[] { 1, 2, 3 },
            "GeneratedApp.sln",
            "src/backend/GeneratedApp.Api.csproj",
            "src/frontend",
            CancellationToken.None));

        Assert.Contains("E2B--TemplateId", error.Message, StringComparison.Ordinal);
        Assert.Null(client.TemplateId);
    }

    [Fact]
    public async Task E2BSandboxClient_UsesSeparateControlAndTrafficTokens()
    {
        var handler = new RecordingHandler();
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["E2B:ApiKey"] = "e2b_test_api_key"
            })
            .Build();
        var client = new E2BSandboxClient(
            http,
            configuration,
            NullLogger<E2BSandboxClient>.Instance);

        BuildProjectResponse result;
        await using (var sandbox = await client.CreateAsync(
            "mnaiwork-software-factory",
            TimeSpan.FromMinutes(17),
            CancellationToken.None))
        {
            result = await sandbox.BuildAsync(
                new BuildProjectRequest("AQID", "app.sln", "api.csproj", "frontend"),
                CancellationToken.None);
        }

        Assert.True(result.Succeeded);
        Assert.Equal(3, handler.Requests.Count);
        var create = handler.Requests[0];
        Assert.Equal("https://api.e2b.app/sandboxes", create.Uri);
        Assert.Equal("e2b_test_api_key", create.Header("X-API-Key"));
        using (var body = JsonDocument.Parse(create.Body))
        {
            Assert.Equal("mnaiwork-software-factory", body.RootElement.GetProperty("templateID").GetString());
            Assert.Equal(1_020, body.RootElement.GetProperty("timeout").GetInt32());
            Assert.True(body.RootElement.GetProperty("secure").GetBoolean());
            Assert.False(body.RootElement.GetProperty("network").GetProperty("allowPublicTraffic").GetBoolean());
        }

        var build = handler.Requests[1];
        Assert.Equal("https://3000-sandbox-123.e2b.app/build", build.Uri);
        Assert.Equal("traffic-token", build.Header("e2b-traffic-access-token"));
        Assert.Null(build.Header("X-API-Key"));

        var delete = handler.Requests[2];
        Assert.Equal(HttpMethod.Delete, delete.Method);
        Assert.Equal("https://api.e2b.app/sandboxes/sandbox-123", delete.Uri);
        Assert.Equal("e2b_test_api_key", delete.Header("X-API-Key"));
    }

    [Fact]
    public async Task E2BSandboxClient_DeletesSandboxWhenCreateResponseOmitsTrafficToken()
    {
        var handler = new RecordingHandler { OmitTrafficToken = true };
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["E2B:ApiKey"] = "e2b_test_api_key"
            })
            .Build();
        var client = new E2BSandboxClient(
            http,
            configuration,
            NullLogger<E2BSandboxClient>.Instance);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.CreateAsync(
            "mnaiwork-software-factory",
            TimeSpan.FromMinutes(17),
            CancellationToken.None));

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(HttpMethod.Delete, handler.Requests[1].Method);
        Assert.Equal(
            "https://api.e2b.app/sandboxes/sandbox-123",
            handler.Requests[1].Uri);
    }

    private static IConfiguration BuildConfiguration() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["BuildExecution:Enabled"] = "true",
            ["E2B:TemplateId"] = "mnaiwork-software-factory",
            ["BuildExecution:MaxConcurrentBuilds"] = "1",
            ["BuildExecution:CommandTimeoutMinutes"] = "6",
            ["BuildExecution:TotalTimeoutMinutes"] = "17",
            ["BuildExecution:PlaywrightVersion"] = "1.62.1"
        })
        .Build();

    private static BuildProjectResponse SuccessfulResponse() => new(
        true,
        "passed",
        Array.Empty<BuildStepResult>(),
        "AQ==",
        "Ag==");

    private sealed class FakeSandboxClient : IE2BSandboxClient
    {
        private readonly BuildProjectResponse _response;

        public FakeSandboxClient(BuildProjectResponse response) => _response = response;

        public string? TemplateId { get; private set; }
        public TimeSpan Timeout { get; private set; }
        public int CreateCount { get; private set; }
        public List<FakeSession> Sessions { get; } = new();
        public FakeSession Session => Sessions[0];

        public Task<IE2BSandboxSession> CreateAsync(
            string templateId,
            TimeSpan timeout,
            CancellationToken ct)
        {
            CreateCount++;
            TemplateId = templateId;
            Timeout = timeout;
            var session = new FakeSession(_response);
            Sessions.Add(session);
            return Task.FromResult<IE2BSandboxSession>(session);
        }
    }

    private sealed class FakeSession : IE2BSandboxSession
    {
        public FakeSession(BuildProjectResponse response) => Response = response;

        public BuildProjectRequest? Request { get; private set; }
        public List<BuildProjectRequest> Requests { get; } = new();
        public int BuildCount { get; private set; }
        public bool Disposed { get; private set; }
        public BuildProjectResponse Response { get; set; }

        public Task<BuildProjectResponse> BuildAsync(BuildProjectRequest request, CancellationToken ct)
        {
            BuildCount++;
            Request = request;
            Requests.Add(request);
            return Task.FromResult(Response);
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = new();
        public bool OmitTrafficToken { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            var headers = request.Content is null
                ? request.Headers.AsEnumerable()
                : request.Headers.Concat(request.Content.Headers);
            Requests.Add(new CapturedRequest(
                request.Method,
                request.RequestUri!.AbsoluteUri,
                headers.ToDictionary(
                    item => item.Key,
                    item => string.Join(",", item.Value),
                    StringComparer.OrdinalIgnoreCase),
                body));

            if (request.Method == HttpMethod.Post
                && request.RequestUri.Host == "api.e2b.app")
            {
                                return Json(
                                        HttpStatusCode.Created,
                                        OmitTrafficToken
                                                ? """{ "sandboxID": "sandbox-123" }"""
                                                : """
                                                    {
                                                        "sandboxID": "sandbox-123",
                                                        "trafficAccessToken": "traffic-token"
                                                    }
                                                    """);
            }
            if (request.Method == HttpMethod.Post && request.RequestUri.AbsolutePath == "/build")
            {
                return Json(HttpStatusCode.OK, JsonSerializer.Serialize(
                    SuccessfulResponse(),
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            }
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
    }

    private sealed record CapturedRequest(
        HttpMethod Method,
        string Uri,
        IReadOnlyDictionary<string, string> Headers,
        string Body)
    {
        public string? Header(string name) => Headers.GetValueOrDefault(name);
    }
}