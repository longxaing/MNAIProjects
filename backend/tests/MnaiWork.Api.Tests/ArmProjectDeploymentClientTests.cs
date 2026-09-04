using System.Net;
using System.Net.Http.Headers;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Azure.Core;
using MnaiWork.Api.Configuration;
using MnaiWork.Api.Deployment;
using Microsoft.Extensions.Options;
using Xunit;

namespace MnaiWork.Api.Tests;

public sealed class ArmProjectDeploymentClientTests
{
    private const string SubscriptionId = "11111111-1111-1111-1111-111111111111";
    private const string TenantId = "22222222-2222-2222-2222-222222222222";
    private const string PrincipalId = "33333333-3333-3333-3333-333333333333";
    private const string ResourceGroup = "rg-generated";

    [Fact]
    public async Task GetGeneratedResourceAsync_RejectsResourceOutsideConfiguredGroup()
    {
        var handler = new StubHandler("{}");
        var client = CreateClient(handler);

        await Assert.ThrowsAsync<ArgumentException>(() => client.GetGeneratedResourceAsync(
            $"/subscriptions/{SubscriptionId}/resourceGroups/other-rg/providers/" +
            "Microsoft.Storage/storageAccounts/example"));

        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task GetGeneratedResourceAsync_RedactsSensitiveFields()
    {
        const string response = """
        {
          "id": "/safe/id",
          "name": "example",
          "type": "Microsoft.Storage/storageAccounts",
          "properties": {
            "provisioningState": "Succeeded",
            "primaryKey": "do-not-return",
            "connectionString": "do-not-return",
            "nested": {
              "password": "do-not-return",
              "secretValue": "do-not-return"
            }
          }
        }
        """;
        var client = CreateClient(new StubHandler(response));
        var resourceId =
            $"/subscriptions/{SubscriptionId}/resourceGroups/{ResourceGroup}/providers/" +
            "Microsoft.Storage/storageAccounts/example";

        var result = await client.GetGeneratedResourceAsync(resourceId);
        var json = result.GetRawText();

        Assert.DoesNotContain("do-not-return", json, StringComparison.Ordinal);
        Assert.Contains("[redacted]", json, StringComparison.Ordinal);
        Assert.Contains("Succeeded", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhatIfAsync_PollsRelativeLocationAndHonorsRetryAfter()
    {
        var handler = new ScriptedHandler((request, call) => call switch
        {
            1 => AcceptedResponse("/operations/what-if-1", TimeSpan.Zero),
            2 => AcceptedResponse(null, TimeSpan.Zero),
            3 => JsonResponse(HttpStatusCode.OK, """
                { "status": "sUcCeEdEd", "properties": { "changes": [] } }
                """),
            _ => throw new InvalidOperationException($"Unexpected ARM call {call}: {request.RequestUri}")
        });
        var client = CreateClient(handler);

        var result = await client.WhatIfAsync(
            new AzureProjectDeploymentRequest { ProjectSlug = "demo-one" },
            "backend-hash",
            "frontend-hash");

        Assert.Equal("sUcCeEdEd", result.Changes.GetProperty("status").GetString());
        Assert.Equal(3, handler.RequestCount);
        Assert.Contains(
            $"/subscriptions/{SubscriptionId}/providers/Microsoft.Resources/deployments/",
            handler.Requests[0].AbsoluteUri,
            StringComparison.Ordinal);
        using (var body = JsonDocument.Parse(handler.Bodies[0]))
        {
            Assert.Equal("eastus2", body.RootElement.GetProperty("location").GetString());
            var properties = body.RootElement.GetProperty("properties");
            Assert.Equal(
                ResourceGroup,
                properties.GetProperty("parameters")
                    .GetProperty("generatedResourceGroupName")
                    .GetProperty("value")
                    .GetString());
            Assert.Equal(
                result.DeploymentFingerprint,
                properties.GetProperty("parameters")
                    .GetProperty("deploymentFingerprint")
                    .GetProperty("value")
                    .GetString());
            var resources = properties.GetProperty("template").GetProperty("resources");
            Assert.Contains(resources.EnumerateArray(), resource =>
                resource.GetProperty("type").GetString() == "Microsoft.Resources/resourceGroups");
            Assert.True(resources.EnumerateArray().Count(resource =>
                resource.GetProperty("type").GetString() == "Microsoft.Resources/deployments") >= 2);
        }
        Assert.Equal(
            "https://management.azure.com/operations/what-if-1",
            handler.Requests[1].ToString());
    }

    [Fact]
    public async Task DeployAsync_RejectsFingerprintMismatchBeforeArmRequest()
    {
        var handler = new StubHandler("{}");
        var client = CreateClient(handler);
        const string buildId = "0123456789abcdef0123456789abcdef";
        var backend = CreateZip(
            ("GeneratedApp.dll", "binary"),
            ("deployment-manifest.json", $$"""{ "buildId": "{{buildId}}" }"""));
        var frontend = CreateZip(
            ("index.html", "<html></html>"),
            ("runtime-config.js", "window.__APP_CONFIG__ = {};"),
            ("build-manifest.json", $$"""{ "buildId": "{{buildId}}" }"""));

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.DeployAsync(
            new AzureProjectDeploymentRequest { ProjectSlug = "demo-one" },
            backend,
            frontend,
            "stale-fingerprint"));

        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task DeployAsync_RejectsMismatchedPackageBuildIds()
    {
        var handler = new StubHandler("{}");
        var client = CreateClient(handler);
        var backend = CreateZip(
            ("deployment-manifest.json", """{ "buildId": "0123456789abcdef0123456789abcdef" }"""));
        var frontend = CreateZip(
            ("index.html", "<html></html>"),
            ("runtime-config.js", "window.__APP_CONFIG__ = {};"),
            ("build-manifest.json", """{ "buildId": "fedcba9876543210fedcba9876543210" }"""));

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => client.DeployAsync(
            new AzureProjectDeploymentRequest { ProjectSlug = "demo-one" },
            backend,
            frontend,
            "any-fingerprint"));

        Assert.Contains("different build IDs", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task DeployAsync_RejectsCredentialWhoseObjectIdDoesNotMatchProfile()
    {
        var handler = new StubHandler("{}");
        var client = CreateClient(
            handler,
            new StubCredential("44444444-4444-4444-4444-444444444444"));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.DeployAsync(
            new AzureProjectDeploymentRequest { ProjectSlug = "demo-one" },
            Array.Empty<byte>(),
            Array.Empty<byte>(),
            "unused"));

        Assert.Contains("does not match", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task WaitForDeploymentAsync_PollsUntilSucceededCaseInsensitively()
    {
        var handler = new ScriptedHandler((_, call) => call switch
        {
            1 => DeploymentResponse("accepted", TimeSpan.Zero),
            2 => DeploymentResponse("rUnNiNg", retryAt: DateTimeOffset.UtcNow.AddSeconds(-1)),
            3 => DeploymentResponse("sUcCeEdEd", TimeSpan.Zero, """
                { "appUrl": { "type": "String", "value": "https://api.example" } }
                """),
            _ => throw new InvalidOperationException($"Unexpected deployment poll {call}.")
        });
        var client = CreateClient(handler);

        var result = await client.WaitForDeploymentAsync("deployment-one", CancellationToken.None);

        Assert.Equal("sUcCeEdEd", result.ProvisioningState);
        Assert.Equal(
            "https://api.example",
            result.Outputs.GetProperty("appUrl").GetProperty("value").GetString());
        Assert.Equal(3, handler.RequestCount);
    }

    [Fact]
    public void ValidateZipPackage_RequiresRuntimeConfigurationForFrontend()
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            archive.CreateEntry("index.html");
        }

        var error = Assert.Throws<InvalidDataException>(() =>
            ArmProjectDeploymentClient.ValidateZipPackage(
                output.ToArray(), "frontend", requireRootIndex: true));

        Assert.Contains("runtime-config.js", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateRuntimeConfig_UsesTrustedAppUrlWithoutTrailingSlash()
    {
        var script = Encoding.UTF8.GetString(
            ArmProjectDeploymentClient.CreateRuntimeConfig(
                "https://api.example/", "approved-fingerprint"));

        Assert.Equal(
            "window.__APP_CONFIG__ = {\"apiBaseUrl\":\"https://api.example\"," +
            "\"deploymentFingerprint\":\"approved-fingerprint\"};\n",
            script);
    }

    [Fact]
    public void ValidateZipPackage_RejectsDuplicateNormalizedPaths()
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            archive.CreateEntry("index.html");
            archive.CreateEntry("INDEX.HTML");
            archive.CreateEntry("runtime-config.js");
        }

        var error = Assert.Throws<InvalidDataException>(() =>
            ArmProjectDeploymentClient.ValidateZipPackage(
                output.ToArray(), "frontend", requireRootIndex: true));

        Assert.Contains("duplicate", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static ArmProjectDeploymentClient CreateClient(
        HttpMessageHandler handler,
        TokenCredential? credential = null)
    {
        var options = Options.Create(new AzureProvisioningOptions
        {
            Enabled = true,
            TenantId = TenantId,
            SubscriptionId = SubscriptionId,
            GeneratedResourceGroup = ResourceGroup,
            Location = "eastus2",
            AppServicePlanName = "shared-plan",
            DeploymentPrincipalId = PrincipalId
        });
        return new ArmProjectDeploymentClient(
            new HttpClient(handler),
            credential ?? new StubCredential(PrincipalId),
            RuntimeAzureProvisioningOptions.Fixed(options.Value));
    }

    private static byte[] CreateZip(params (string Name, string Content)[] files)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in files)
            {
                var entry = archive.CreateEntry(file.Name);
                using var writer = new StreamWriter(entry.Open());
                writer.Write(file.Content);
            }
        }
        return output.ToArray();
    }

    private sealed class StubCredential : TokenCredential
    {
        private readonly string _token;

        public StubCredential(string objectId) => _token = CreateToken(objectId);

        public override AccessToken GetToken(
            TokenRequestContext requestContext,
            CancellationToken cancellationToken) =>
            new(_token, DateTimeOffset.UtcNow.AddHours(1));

        public override ValueTask<AccessToken> GetTokenAsync(
            TokenRequestContext requestContext,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));

        private static string CreateToken(string objectId)
        {
            static string Encode(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
                .TrimEnd('=').Replace('+', '-').Replace('/', '_');
            return $"{Encode("{\"alg\":\"none\"}")}." +
                     $"{Encode($$"""{ "oid": "{{objectId}}" }""")}.signature";
        }
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly string _response;

        public StubHandler(string response) => _response = response;

        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_response, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, int, HttpResponseMessage> _responseFactory;

        public ScriptedHandler(Func<HttpRequestMessage, int, HttpResponseMessage> responseFactory)
            => _responseFactory = responseFactory;

        public int RequestCount { get; private set; }
        public List<Uri> Requests { get; } = new();
        public List<string> Bodies { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            Requests.Add(request.RequestUri!);
            Bodies.Add(request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken));
            return _responseFactory(request, RequestCount);
        }
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, string json)
        => new(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private static HttpResponseMessage AcceptedResponse(string? location, TimeSpan retryAfter)
    {
        var response = JsonResponse(HttpStatusCode.Accepted, "{}");
        if (location is not null)
        {
            response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
        }
        response.Headers.RetryAfter = new RetryConditionHeaderValue(retryAfter);
        return response;
    }

    private static HttpResponseMessage DeploymentResponse(
        string state,
        TimeSpan? retryAfter = null,
        string outputs = "{}",
        DateTimeOffset? retryAt = null)
    {
        var response = JsonResponse(HttpStatusCode.OK, $$"""
            {
              "properties": {
                "provisioningState": "{{state}}",
                "outputs": {{outputs}}
              }
            }
            """);
        if (retryAfter is not null)
        {
            response.Headers.RetryAfter = new RetryConditionHeaderValue(retryAfter.Value);
        }
        else if (retryAt is not null)
        {
            response.Headers.RetryAfter = new RetryConditionHeaderValue(retryAt.Value);
        }
        return response;
    }
}