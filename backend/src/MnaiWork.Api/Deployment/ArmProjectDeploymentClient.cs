using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Azure;
using Azure.Core;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using MnaiWork.Api.Configuration;
using Microsoft.Extensions.Options;

namespace MnaiWork.Api.Deployment;

public sealed class AzureProjectDeploymentRequest
{
    public string ProjectSlug { get; set; } = string.Empty;
    public string CosmosDatabaseName { get; set; } = "app";
    public string CosmosContainerName { get; set; } = "items";
    public string BackendPackageFileId { get; set; } = string.Empty;
    public string FrontendPackageFileId { get; set; } = string.Empty;
    public string HealthCheckPath { get; set; } = "/health";
}

public sealed record AzureProjectDeploymentResult(
    string DeploymentName,
    string ProvisioningState,
    JsonElement Outputs,
    string SubscriptionId,
    string ResourceGroup);

public sealed record AzureProjectWhatIfResult(
    JsonElement Changes,
    string DeploymentFingerprint);

public sealed class ArmProjectDeploymentClient
{
    private const string ArmScope = "https://management.azure.com/.default";
    private const string ArmApiVersion = "2022-09-01";
    private const string PublicationContractVersion = "2";
    private const string TemplateResourceName =
        "MnaiWork.Api.Deployment.Templates.generated-deployment.json";

    private static readonly Regex ProjectSlugPattern = new(
        "^[a-z0-9](?:[a-z0-9-]{1,22}[a-z0-9])?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex CosmosNamePattern = new(
        "^[A-Za-z0-9_-]{1,64}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> DeploymentGates = new();

    private readonly HttpClient _http;
    private readonly TokenCredential _credential;
    private readonly RuntimeAzureProvisioningOptions _options;
    private readonly AzureProvisioningOperationGate _operationGate;
    private readonly string _templateJson;

    public ArmProjectDeploymentClient(
        HttpClient http,
        TokenCredential credential,
        RuntimeAzureProvisioningOptions options,
        AzureProvisioningOperationGate? operationGate = null)
    {
        _http = http;
        _credential = credential;
        _options = options;
        _operationGate = operationGate ?? new AzureProvisioningOperationGate();
        _templateJson = LoadTemplate();
    }

    public bool Enabled => _options.Enabled;
    public string TenantId => _options.TenantId;
    public string SubscriptionId => _options.SubscriptionId;
    public string GeneratedResourceGroup => _options.GeneratedResourceGroup;

    public async Task<JsonElement> ListGeneratedResourcesAsync(CancellationToken ct = default)
    {
        ValidateConfiguration();
        await EnsureDeploymentTargetExistsAsync(ct);

        var resources = new JsonArray();
        Uri? next = new(
            $"https://management.azure.com/subscriptions/{_options.SubscriptionId}" +
            $"/resourceGroups/{Uri.EscapeDataString(_options.GeneratedResourceGroup)}" +
            "/resources?api-version=2021-04-01");

        while (next is not null && resources.Count < 500)
        {
            using var response = await SendAsync(HttpMethod.Get, next, null, ct);
            var page = await ReadSuccessJsonAsync(response, ct);
            if (page.TryGetProperty("value", out var values))
            {
                foreach (var resource in values.EnumerateArray())
                {
                    var type = resource.TryGetProperty("type", out var typeElement)
                        ? typeElement.GetString()
                        : null;
                    if (type is null || !IsListableResourceType(type))
                    {
                        continue;
                    }

                    resources.Add(new JsonObject
                    {
                        ["id"] = GetOptionalString(resource, "id"),
                        ["name"] = GetOptionalString(resource, "name"),
                        ["type"] = type,
                        ["location"] = GetOptionalString(resource, "location"),
                        ["kind"] = GetOptionalString(resource, "kind"),
                        ["provisioningState"] = GetNestedOptionalString(
                            resource, "properties", "provisioningState")
                    });
                }
            }

            next = page.TryGetProperty("nextLink", out var nextLink)
                && Uri.TryCreate(nextLink.GetString(), UriKind.Absolute, out var nextUri)
                    ? nextUri
                    : null;
        }

        return ToElement(new JsonObject
        {
            ["subscriptionId"] = _options.SubscriptionId,
            ["resourceGroup"] = _options.GeneratedResourceGroup,
            ["resources"] = resources
        });
    }

    public async Task<JsonElement> GetGeneratedResourceAsync(
        string resourceId,
        CancellationToken ct = default)
    {
        ValidateConfiguration();
        var expectedPrefix =
            $"/subscriptions/{_options.SubscriptionId}/resourceGroups/{_options.GeneratedResourceGroup}/providers/";
        if (string.IsNullOrWhiteSpace(resourceId)
            || resourceId.Contains('?', StringComparison.Ordinal)
            || resourceId.Contains('#', StringComparison.Ordinal)
            || !resourceId.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "ResourceId must identify a resource in the configured Generated Resource Group.");
        }

        var resourceType = GetResourceType(resourceId);
        var apiVersion = ResourceApiVersion(resourceType)
            ?? throw new ArgumentException($"Resource type '{resourceType}' is not allowed.");
        var uri = new Uri(
            $"https://management.azure.com{resourceId}?api-version={Uri.EscapeDataString(apiVersion)}");
        using var response = await SendAsync(HttpMethod.Get, uri, null, ct);
        var detail = await ReadSuccessJsonAsync(response, ct);
        var node = JsonNode.Parse(detail.GetRawText())
            ?? throw new InvalidOperationException("Azure returned an empty resource response.");
        RedactSensitiveProperties(node);
        return ToElement(node);
    }

    public string GetDeploymentFingerprint(
        AzureProjectDeploymentRequest request,
        string backendPackageHash,
        string frontendPackageHash)
    {
        var canonical = string.Join('\n',
            PublicationContractVersion,
            _templateJson,
            _options.TenantId,
            _options.SubscriptionId,
            _options.GeneratedResourceGroup,
            _options.Location,
            _options.AppServicePlanName,
            _options.DeploymentPrincipalId,
            request.ProjectSlug,
            request.CosmosDatabaseName,
            request.CosmosContainerName,
            request.HealthCheckPath,
            backendPackageHash,
            frontendPackageHash);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant();
    }

    public async Task<AzureProjectWhatIfResult> WhatIfAsync(
        AzureProjectDeploymentRequest request,
        string backendPackageHash,
        string frontendPackageHash,
        CancellationToken ct = default)
    {
        using var operation = await _operationGate.EnterAsync(ct);
        Validate(request);
        var deploymentFingerprint = GetDeploymentFingerprint(
            request, backendPackageHash, frontendPackageHash);
        var deploymentName = CreateDeploymentName(request.ProjectSlug);
        var uri = new Uri($"{DeploymentUri(deploymentName)}/whatIf?api-version={ArmApiVersion}");
        var payload = BuildPayload(request, deploymentFingerprint, includeWhatIfFormat: true);
        using var response = await SendAsync(HttpMethod.Post, uri, payload, ct);

        if (response.StatusCode == HttpStatusCode.Accepted)
        {
            return new AzureProjectWhatIfResult(
                await PollOperationAsync(response, ct), deploymentFingerprint);
        }

        return new AzureProjectWhatIfResult(
            await ReadSuccessJsonAsync(response, ct), deploymentFingerprint);
    }

    public async Task<AzureProjectDeploymentResult> DeployAsync(
        AzureProjectDeploymentRequest request,
        byte[] backendPackage,
        byte[] frontendPackage,
        string deploymentFingerprint,
        CancellationToken ct = default)
    {
        using var operation = await _operationGate.EnterAsync(ct);
        Validate(request);
        await ValidateDeploymentIdentityAsync(ct);
        ValidateZipPackage(backendPackage, "backend", requireRootIndex: false);
        ValidateZipPackage(frontendPackage, "frontend", requireRootIndex: true);
        var backendBuildId = GetPackageBuildId(backendPackage, "deployment-manifest.json");
        var frontendBuildId = GetPackageBuildId(frontendPackage, "build-manifest.json");
        if (!string.Equals(backendBuildId, frontendBuildId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Backend and frontend packages have different build IDs.");
        }
        var actualFingerprint = GetDeploymentFingerprint(
            request,
            Convert.ToHexString(SHA256.HashData(backendPackage)).ToLowerInvariant(),
            Convert.ToHexString(SHA256.HashData(frontendPackage)).ToLowerInvariant());
        if (!string.Equals(
                actualFingerprint, deploymentFingerprint, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Deployment configuration or packages changed after ARM what-if approval.");
        }
        var gateKey = string.Join('/',
            _options.SubscriptionId,
            _options.GeneratedResourceGroup,
            request.ProjectSlug).ToLowerInvariant();
        var gate = DeploymentGates.GetOrAdd(gateKey, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            return await DeployCoreAsync(
                request,
                backendPackage,
                frontendPackage,
                deploymentFingerprint,
                backendBuildId,
                ct);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<AzureProjectDeploymentResult> DeployCoreAsync(
        AzureProjectDeploymentRequest request,
        byte[] backendPackage,
        byte[] frontendPackage,
        string deploymentFingerprint,
        string buildId,
        CancellationToken ct)
    {
        var deploymentName = CreateDeploymentName(request.ProjectSlug);
        var uri = new Uri($"{DeploymentUri(deploymentName)}?api-version={ArmApiVersion}");
        var payload = BuildPayload(request, deploymentFingerprint, includeWhatIfFormat: false);
        using var response = await SendAsync(HttpMethod.Put, uri, payload, ct);
        await EnsureSuccessAsync(response, ct);

        var result = await WaitForDeploymentAsync(deploymentName, ct);
        try
        {
            await EnableStaticWebsiteAsync(result.Outputs, ct);
            await PublishBackendWithRetryAsync(result.Outputs, backendPackage, ct);
            var appUrl = GetOutputString(result.Outputs, "appUrl");
            var frontendUrl = await PublishFrontendWithRetryAsync(
                result.Outputs, frontendPackage, appUrl, deploymentFingerprint, ct);
            await VerifyBackendFingerprintAsync(
                new Uri(new Uri(appUrl), request.HealthCheckPath), deploymentFingerprint, buildId, ct);
            await VerifyBackendFingerprintAsync(
                new Uri(
                    new Uri(appUrl),
                    $"/ready?fingerprint={Uri.EscapeDataString(deploymentFingerprint)}"),
                deploymentFingerprint,
                buildId,
                ct);
            await VerifyFrontendFingerprintAsync(frontendUrl, deploymentFingerprint, ct);
            await VerifyFrontendBuildIdAsync(frontendUrl, buildId, ct);
            await VerifyEndpointAsync(new Uri(frontendUrl), "frontend index", ct);
            return result with
            {
                Outputs = AddPublicationOutputs(result.Outputs, frontendUrl)
            };
        }
        catch (Exception ex) when (ex is RequestFailedException
            or HttpRequestException
            or InvalidDataException
            or InvalidOperationException
            or TimeoutException)
        {
            throw new InvalidOperationException(
                $"Infrastructure deployment '{deploymentName}' succeeded, but application publication failed. " +
                "The deployment is idempotent; fix the package or permission issue and rerun the same project slug. " +
                ex.Message,
                ex);
        }
    }

    private void Validate(AzureProjectDeploymentRequest request)
    {
        ValidateConfiguration();

        if (!ProjectSlugPattern.IsMatch(request.ProjectSlug))
        {
            throw new ArgumentException(
                "ProjectSlug must be 3-24 lowercase letters, numbers, or hyphens, " +
                "and must start and end with a letter or number.");
        }

        if (!CosmosNamePattern.IsMatch(request.CosmosDatabaseName)
            || !CosmosNamePattern.IsMatch(request.CosmosContainerName))
        {
            throw new ArgumentException(
                "Cosmos database and container names must be 1-64 letters, numbers, underscores, or hyphens.");
        }

        if (!request.HealthCheckPath.StartsWith("/", StringComparison.Ordinal)
            || request.HealthCheckPath.StartsWith("//", StringComparison.Ordinal)
            || request.HealthCheckPath.Contains('?')
            || request.HealthCheckPath.Contains('#'))
        {
            throw new ArgumentException("HealthCheckPath must be an absolute application path without a query or fragment.");
        }
    }

    private void ValidateConfiguration()
    {
        if (!_options.Enabled)
        {
            throw new InvalidOperationException("Azure project provisioning is disabled.");
        }

        if (!Guid.TryParse(_options.TenantId, out _)
            || !Guid.TryParse(_options.SubscriptionId, out _)
            || !Guid.TryParse(_options.DeploymentPrincipalId, out _)
            || string.IsNullOrWhiteSpace(_options.GeneratedResourceGroup)
            || string.IsNullOrWhiteSpace(_options.AppServicePlanName))
        {
            throw new InvalidOperationException(
                "AzureProvisioning configuration is incomplete or invalid.");
        }
    }

    private async Task ValidateDeploymentIdentityAsync(CancellationToken ct)
    {
        var token = await _credential.GetTokenAsync(
            new TokenRequestContext(new[] { ArmScope }), ct);
        var segments = token.Token.Split('.');
        if (segments.Length < 2)
        {
            throw new InvalidOperationException(
                "Azure deployment credential returned an unreadable access token.");
        }
        try
        {
            var payload = segments[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var document = JsonDocument.Parse(Convert.FromBase64String(payload));
            var objectId = document.RootElement.TryGetProperty("oid", out var oid)
                ? oid.GetString()
                : null;
            if (!string.Equals(
                    objectId, _options.DeploymentPrincipalId, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "The active Azure credential does not match AzureProvisioning:DeploymentPrincipalId. " +
                    "Attach/select the configured managed identity, or use the matching local service principal.");
            }
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException(
                "Azure deployment credential returned an invalid access token.", ex);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                "Azure deployment credential returned an invalid access token payload.", ex);
        }
    }

    private static bool IsListableResourceType(string type)
        => ResourceApiVersion(type) is not null;

    private static string? ResourceApiVersion(string type) => type.ToLowerInvariant() switch
    {
        "microsoft.web/sites" => "2024-11-01",
        "microsoft.web/serverfarms" => "2024-11-01",
        "microsoft.storage/storageaccounts" => "2023-05-01",
        "microsoft.storage/storageaccounts/blobservices" => "2023-05-01",
        "microsoft.storage/storageaccounts/blobservices/containers" => "2023-05-01",
        "microsoft.documentdb/databaseaccounts" => "2024-05-15",
        "microsoft.documentdb/databaseaccounts/sqldatabases" => "2024-05-15",
        "microsoft.documentdb/databaseaccounts/sqldatabases/containers" => "2024-05-15",
        "microsoft.documentdb/databaseaccounts/sqlroleassignments" => "2024-05-15",
        "microsoft.keyvault/vaults" => "2023-07-01",
        "microsoft.authorization/roleassignments" => "2022-04-01",
        _ => null
    };

    private static string GetResourceType(string resourceId)
    {
        var providerIndex = resourceId.LastIndexOf("/providers/", StringComparison.OrdinalIgnoreCase);
        var segments = resourceId[(providerIndex + "/providers/".Length)..]
            .Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 3 || segments.Length % 2 == 0)
        {
            throw new ArgumentException("ResourceId has an invalid Azure resource path.");
        }

        var types = new List<string> { segments[0] };
        for (var index = 1; index < segments.Length; index += 2)
        {
            types.Add(segments[index]);
        }
        return string.Join('/', types);
    }

    private static void RedactSensitiveProperties(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            foreach (var property in obj.ToList())
            {
                if (IsSensitiveProperty(property.Key))
                {
                    obj[property.Key] = "[redacted]";
                }
                else if (property.Value is not null)
                {
                    RedactSensitiveProperties(property.Value);
                }
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var item in array.Where(item => item is not null))
            {
                RedactSensitiveProperties(item!);
            }
        }
    }

    private static bool IsSensitiveProperty(string name)
    {
        var normalized = name.Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();
        return normalized.Contains("password", StringComparison.Ordinal)
            || normalized.Contains("secret", StringComparison.Ordinal)
            || normalized.Contains("connectionstring", StringComparison.Ordinal)
            || normalized is "key" or "keys" or "primarykey" or "secondarykey"
                or "accesskey" or "publishingpassword";
    }

    private static string? GetOptionalString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) ? value.GetString() : null;

    private static string? GetNestedOptionalString(
        JsonElement element,
        string parent,
        string name)
        => element.TryGetProperty(parent, out var parentElement)
            && parentElement.TryGetProperty(name, out var value)
                ? value.GetString()
                : null;

    private static JsonElement ToElement(JsonNode node)
    {
        using var document = JsonDocument.Parse(node.ToJsonString());
        return document.RootElement.Clone();
    }

    private JsonObject BuildPayload(
        AzureProjectDeploymentRequest request,
        string deploymentFingerprint,
        bool includeWhatIfFormat)
    {
        var properties = new JsonObject
        {
            ["mode"] = "Incremental",
            ["template"] = JsonNode.Parse(_templateJson),
            ["parameters"] = new JsonObject
            {
                ["generatedResourceGroupName"] = Parameter(_options.GeneratedResourceGroup),
                ["projectSlug"] = Parameter(request.ProjectSlug),
                ["location"] = Parameter(_options.Location),
                ["appServicePlanName"] = Parameter(_options.AppServicePlanName),
                ["deploymentPrincipalId"] = Parameter(_options.DeploymentPrincipalId),
                ["cosmosDatabaseName"] = Parameter(request.CosmosDatabaseName),
                ["cosmosContainerName"] = Parameter(request.CosmosContainerName),
                ["healthCheckPath"] = Parameter(request.HealthCheckPath),
                ["deploymentFingerprint"] = Parameter(deploymentFingerprint)
            }
        };

        if (includeWhatIfFormat)
        {
            properties["whatIfSettings"] = new JsonObject
            {
                ["resultFormat"] = "FullResourcePayloads"
            };
        }

        return new JsonObject
        {
            ["location"] = _options.Location,
            ["properties"] = properties
        };
    }

    internal async Task<AzureProjectDeploymentResult> WaitForDeploymentAsync(
        string deploymentName,
        CancellationToken ct)
    {
        var uri = new Uri($"{DeploymentUri(deploymentName)}?api-version={ArmApiVersion}");
        var deadline = DateTimeOffset.UtcNow.AddMinutes(Math.Clamp(_options.TimeoutMinutes, 1, 120));
        var retryAfter = TimeSpan.Zero;

        while (DateTimeOffset.UtcNow < deadline)
        {
            if (retryAfter > TimeSpan.Zero)
            {
                await Task.Delay(retryAfter, ct);
            }
            using var response = await SendAsync(HttpMethod.Get, uri, null, ct);
            retryAfter = GetRetryDelay(response, TimeSpan.FromSeconds(2));
            var result = await ReadSuccessJsonAsync(response, ct);
            var properties = result.GetProperty("properties");
            var state = properties.GetProperty("provisioningState").GetString() ?? "Unknown";

            if (IsState(state, "Succeeded"))
            {
                var outputs = properties.TryGetProperty("outputs", out var value)
                    ? value.Clone()
                    : EmptyObject();
                return new AzureProjectDeploymentResult(
                    deploymentName,
                    state,
                    outputs,
                    _options.SubscriptionId,
                    _options.GeneratedResourceGroup);
            }

            if (IsState(state, "Failed", "Canceled", "Cancelled"))
            {
                throw new InvalidOperationException(
                    $"ARM deployment '{deploymentName}' ended in state '{state}': {properties}");
            }

            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }

        throw new TimeoutException($"ARM deployment '{deploymentName}' timed out.");
    }

    private async Task<JsonElement> PollOperationAsync(
        HttpResponseMessage initialResponse,
        CancellationToken ct)
    {
        var location = initialResponse.Headers.Location
            ?? throw new InvalidOperationException("ARM what-if response did not include a polling URL.");
        var pollingUri = location.IsAbsoluteUri
            ? location
            : new Uri(new Uri("https://management.azure.com"), location);
        var deadline = DateTimeOffset.UtcNow.AddMinutes(Math.Clamp(_options.TimeoutMinutes, 1, 120));
        var retryAfter = GetRetryDelay(initialResponse, TimeSpan.FromSeconds(2));

        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(retryAfter, ct);
            using var response = await SendAsync(HttpMethod.Get, pollingUri, null, ct);
            retryAfter = GetRetryDelay(response, TimeSpan.FromSeconds(2));
            if (response.StatusCode == HttpStatusCode.Accepted)
            {
                continue;
            }

            var result = await ReadSuccessJsonAsync(response, ct);
            if (result.TryGetProperty("status", out var statusElement))
            {
                var status = statusElement.GetString();
                if (IsState(status, "Running", "Accepted", "InProgress"))
                {
                    continue;
                }
                if (IsState(status, "Failed", "Canceled", "Cancelled"))
                {
                    throw new InvalidOperationException($"ARM what-if failed: {result}");
                }
            }
            return result;
        }

        throw new TimeoutException("ARM what-if operation timed out.");
    }

    private static TimeSpan GetRetryDelay(
        HttpResponseMessage response,
        TimeSpan fallback)
    {
        var retryAfter = response.Headers.RetryAfter;
        var delay = retryAfter?.Delta
            ?? (retryAfter?.Date is { } date
                ? date - DateTimeOffset.UtcNow
                : fallback);
        return TimeSpan.FromMilliseconds(Math.Clamp(
            delay.TotalMilliseconds,
            0,
            TimeSpan.FromSeconds(30).TotalMilliseconds));
    }

    private static bool IsState(string? actual, params string[] expected)
        => actual is not null
            && expected.Any(value => string.Equals(
                actual, value, StringComparison.OrdinalIgnoreCase));

    private async Task EnsureDeploymentTargetExistsAsync(CancellationToken ct)
    {
        var resourceGroupUri = new Uri(
            $"https://management.azure.com/subscriptions/{_options.SubscriptionId}" +
            $"/resourceGroups/{Uri.EscapeDataString(_options.GeneratedResourceGroup)}" +
            "?api-version=2024-03-01");
        await EnsureResourceExistsAsync(resourceGroupUri, "generated resource group", ct);

        var planUri = new Uri(
            $"https://management.azure.com/subscriptions/{_options.SubscriptionId}" +
            $"/resourceGroups/{Uri.EscapeDataString(_options.GeneratedResourceGroup)}" +
            $"/providers/Microsoft.Web/serverfarms/{Uri.EscapeDataString(_options.AppServicePlanName)}" +
            "?api-version=2024-11-01");
        await EnsureResourceExistsAsync(planUri, "shared App Service Plan", ct);
    }

    private async Task EnsureResourceExistsAsync(Uri uri, string resourceDescription, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, uri, null, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new InvalidOperationException(
                $"The {resourceDescription} does not exist. Complete an approved project deployment first.");
        }

        await EnsureSuccessAsync(response, ct);
    }

    private async Task EnableStaticWebsiteAsync(JsonElement outputs, CancellationToken ct)
    {
        var blobEndpoint = GetOutputString(outputs, "storageBlobEndpoint");
        var client = new BlobServiceClient(new Uri(blobEndpoint), _credential);
        var deadline = DateTimeOffset.UtcNow.AddMinutes(Math.Clamp(_options.TimeoutMinutes, 1, 120));

        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                var properties = (await client.GetPropertiesAsync(ct)).Value;
                properties.StaticWebsite = new BlobStaticWebsite
                {
                    Enabled = true,
                    IndexDocument = "index.html",
                    ErrorDocument404Path = "index.html"
                };
                await client.SetPropertiesAsync(properties, ct);
                return;
            }
            catch (RequestFailedException ex) when (ex.Status is 403 or 404 or 409)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
        }

        throw new TimeoutException("Timed out enabling the generated Storage static website.");
    }

    private async Task PublishBackendWithRetryAsync(
        JsonElement outputs,
        byte[] package,
        CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow.AddMinutes(Math.Clamp(_options.TimeoutMinutes, 1, 15));
        var attempt = 0;
        while (true)
        {
            try
            {
                var appName = GetOutputString(outputs, "appName");
                var uri = new Uri(
                    $"https://{Uri.EscapeDataString(appName)}.scm.azurewebsites.net" +
                    "/api/publish?type=zip&clean=true&restart=true");
                var token = await _credential.GetTokenAsync(
                    new TokenRequestContext(new[] { ArmScope }), ct);
                using var request = new HttpRequestMessage(HttpMethod.Post, uri);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
                request.Content = new ByteArrayContent(package);
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
                using var response = await _http.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, ct);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
                if (!IsTransientStatus(response.StatusCode) || DateTimeOffset.UtcNow >= deadline)
                {
                    await EnsureSuccessAsync(response, ct);
                }
                await Task.Delay(GetRetryDelay(response, RetryDelay(attempt++)), ct);
            }
            catch (HttpRequestException) when (DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(RetryDelay(attempt++), ct);
            }
        }
    }

    private async Task<string> PublishFrontendWithRetryAsync(
        JsonElement outputs,
        byte[] package,
        string appUrl,
        string deploymentFingerprint,
        CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow.AddMinutes(Math.Clamp(_options.TimeoutMinutes, 1, 15));
        var attempt = 0;
        while (true)
        {
            try
            {
                return await PublishFrontendAsync(
                    outputs, package, appUrl, deploymentFingerprint, ct);
            }
            catch (RequestFailedException ex) when (
                IsTransientStatus((HttpStatusCode)ex.Status) && DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(RetryDelay(attempt++), ct);
            }
            catch (HttpRequestException) when (DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(RetryDelay(attempt++), ct);
            }
        }
    }

    private static bool IsTransientStatus(HttpStatusCode status) => status is
        HttpStatusCode.RequestTimeout or
        HttpStatusCode.Unauthorized or
        HttpStatusCode.Forbidden or
        HttpStatusCode.NotFound or
        HttpStatusCode.Conflict or
        HttpStatusCode.TooManyRequests or
        HttpStatusCode.InternalServerError or
        HttpStatusCode.BadGateway or
        HttpStatusCode.ServiceUnavailable or
        HttpStatusCode.GatewayTimeout;

    private static TimeSpan RetryDelay(int attempt) => TimeSpan.FromSeconds(
        Math.Min(30, Math.Pow(2, Math.Min(attempt, 5))));

    private async Task<string> PublishFrontendAsync(
        JsonElement outputs,
        byte[] package,
        string appUrl,
        string deploymentFingerprint,
        CancellationToken ct)
    {
        var blobEndpoint = GetOutputString(outputs, "storageBlobEndpoint");
        var storageAccountName = GetOutputString(outputs, "storageAccountName");
        var container = new BlobServiceClient(new Uri(blobEndpoint), _credential)
            .GetBlobContainerClient("$web");
        var deployedNames = new HashSet<string>(StringComparer.Ordinal);

        using var archiveStream = new MemoryStream(package, writable: false);
        using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read, leaveOpen: false);
        var entries = archive.Entries.Where(entry => !string.IsNullOrEmpty(entry.Name)).ToList();
        foreach (var entry in entries.Where(entry =>
                     !string.Equals(
                         NormalizeZipEntry(entry.FullName), "runtime-config.js", StringComparison.OrdinalIgnoreCase)
                     && !string.Equals(
                         NormalizeZipEntry(entry.FullName), "index.html", StringComparison.OrdinalIgnoreCase)))
        {
            var blobName = NormalizeZipEntry(entry.FullName);
            deployedNames.Add(blobName);
            var blob = container.GetBlobClient(blobName);
            await using var content = entry.Open();
            await blob.UploadAsync(content, new BlobUploadOptions
            {
                HttpHeaders = new BlobHttpHeaders
                {
                    ContentType = GetContentType(blobName),
                    CacheControl = string.Equals(blobName, "index.html", StringComparison.OrdinalIgnoreCase)
                        ? "no-cache"
                        : "public, max-age=31536000, immutable"
                }
            }, ct);
        }

        const string runtimeConfigName = "runtime-config.js";
        deployedNames.Add(runtimeConfigName);
        var runtimeConfig = CreateRuntimeConfig(appUrl, deploymentFingerprint);
        await container.GetBlobClient(runtimeConfigName).UploadAsync(
            new BinaryData(runtimeConfig),
            new BlobUploadOptions
            {
                HttpHeaders = new BlobHttpHeaders
                {
                    ContentType = "text/javascript; charset=utf-8",
                    CacheControl = "no-store"
                }
            },
            ct);

        var indexEntry = entries.Single(entry => string.Equals(
            NormalizeZipEntry(entry.FullName), "index.html", StringComparison.OrdinalIgnoreCase));
        deployedNames.Add("index.html");
        await using (var indexContent = indexEntry.Open())
        {
            await container.GetBlobClient("index.html").UploadAsync(
                indexContent,
                new BlobUploadOptions
                {
                    HttpHeaders = new BlobHttpHeaders
                    {
                        ContentType = "text/html; charset=utf-8",
                        CacheControl = "no-cache"
                    }
                },
                ct);
        }

        await foreach (var existing in container.GetBlobsAsync(cancellationToken: ct))
        {
            if (!deployedNames.Contains(existing.Name))
            {
                await container.DeleteBlobIfExistsAsync(existing.Name, cancellationToken: ct);
            }
        }

        return await GetStorageWebEndpointAsync(storageAccountName, ct);
    }

    private async Task<string> GetStorageWebEndpointAsync(string storageAccountName, CancellationToken ct)
    {
        var uri = new Uri(
            $"https://management.azure.com/subscriptions/{_options.SubscriptionId}" +
            $"/resourceGroups/{Uri.EscapeDataString(_options.GeneratedResourceGroup)}" +
            $"/providers/Microsoft.Storage/storageAccounts/{Uri.EscapeDataString(storageAccountName)}" +
            "?api-version=2023-05-01");
        using var response = await SendAsync(HttpMethod.Get, uri, null, ct);
        var account = await ReadSuccessJsonAsync(response, ct);
        if (account.GetProperty("properties").GetProperty("primaryEndpoints")
                .TryGetProperty("web", out var endpoint)
            && endpoint.GetString() is { Length: > 0 } value)
        {
            return value;
        }

        throw new InvalidOperationException("Azure Storage did not return a static website endpoint.");
    }

    private async Task VerifyEndpointAsync(Uri uri, string description, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow.AddMinutes(Math.Clamp(_options.TimeoutMinutes, 1, 15));
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
            }

            await Task.Delay(TimeSpan.FromSeconds(5), ct);
        }

        throw new TimeoutException($"Timed out waiting for the {description} at {uri}.");
    }

    private async Task VerifyBackendFingerprintAsync(
        Uri uri,
        string expectedFingerprint,
        string expectedBuildId,
        CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow.AddMinutes(Math.Clamp(_options.TimeoutMinutes, 1, 15));
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                using var response = await _http.GetAsync(uri, ct);
                if (response.IsSuccessStatusCode)
                {
                    var health = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
                    if (health.TryGetProperty("deploymentFingerprint", out var fingerprint)
                        && string.Equals(
                            fingerprint.GetString(), expectedFingerprint, StringComparison.Ordinal)
                        && health.TryGetProperty("buildId", out var buildId)
                        && string.Equals(buildId.GetString(), expectedBuildId, StringComparison.Ordinal))
                    {
                        return;
                    }
                }
            }
            catch (HttpRequestException)
            {
            }
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
        }
        throw new TimeoutException("Timed out waiting for the approved backend package fingerprint.");
    }

    private async Task VerifyFrontendFingerprintAsync(
        string frontendUrl,
        string expectedFingerprint,
        CancellationToken ct)
    {
        var uri = new Uri(new Uri(frontendUrl), "runtime-config.js");
        var deadline = DateTimeOffset.UtcNow.AddMinutes(Math.Clamp(_options.TimeoutMinutes, 1, 15));
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                using var response = await _http.GetAsync(uri, ct);
                var script = response.IsSuccessStatusCode
                    ? await response.Content.ReadAsStringAsync(ct)
                    : string.Empty;
                if (script.Contains(
                        JsonSerializer.Serialize(expectedFingerprint), StringComparison.Ordinal))
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
            }
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
        }
        throw new TimeoutException("Timed out waiting for the approved frontend package fingerprint.");
    }

    private async Task VerifyFrontendBuildIdAsync(
        string frontendUrl,
        string expectedBuildId,
        CancellationToken ct)
    {
        var uri = new Uri(new Uri(frontendUrl), "build-manifest.json");
        var deadline = DateTimeOffset.UtcNow.AddMinutes(Math.Clamp(_options.TimeoutMinutes, 1, 15));
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                using var response = await _http.GetAsync(uri, ct);
                if (response.IsSuccessStatusCode)
                {
                    var manifest = await response.Content.ReadFromJsonAsync<JsonElement>(
                        cancellationToken: ct);
                    if (manifest.TryGetProperty("buildId", out var buildId)
                        && string.Equals(buildId.GetString(), expectedBuildId, StringComparison.Ordinal))
                    {
                        return;
                    }
                }
            }
            catch (HttpRequestException)
            {
            }
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
        }
        throw new TimeoutException("Timed out waiting for the approved frontend build ID.");
    }

    internal static byte[] CreateRuntimeConfig(
        string appUrl,
        string deploymentFingerprint) => Encoding.UTF8.GetBytes(
        $"window.__APP_CONFIG__ = {JsonSerializer.Serialize(new
        {
            apiBaseUrl = appUrl.TrimEnd('/'),
            deploymentFingerprint
        })};\n");

    internal static void ValidateZipPackage(
        byte[] package,
        string packageName,
        bool requireRootIndex)
    {
        if (package.Length == 0)
        {
            throw new InvalidDataException($"The {packageName} ZIP package is empty.");
        }

        using var stream = new MemoryStream(package, writable: false);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        var files = archive.Entries.Where(entry => !string.IsNullOrEmpty(entry.Name)).ToList();
        if (files.Count == 0 || files.Count > 10_000)
        {
            throw new InvalidDataException($"The {packageName} ZIP package has an invalid file count.");
        }

        long totalLength = 0;
        var normalizedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in files)
        {
            var normalized = NormalizeZipEntry(entry.FullName);
            if (!normalizedNames.Add(normalized))
            {
                throw new InvalidDataException(
                    $"The {packageName} ZIP package contains duplicate path '{normalized}'.");
            }
            totalLength = checked(totalLength + entry.Length);
            if (totalLength > 512L * 1024 * 1024)
            {
                throw new InvalidDataException($"The expanded {packageName} ZIP package exceeds 512 MB.");
            }
        }

        if (requireRootIndex && !files.Any(entry =>
                string.Equals(NormalizeZipEntry(entry.FullName), "index.html", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException("The frontend ZIP package must contain index.html at its root.");
        }
        if (requireRootIndex && !files.Any(entry =>
                string.Equals(
                    NormalizeZipEntry(entry.FullName),
                    "runtime-config.js",
                    StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException(
                "The frontend ZIP package must contain runtime-config.js at its root.");
        }
    }

    internal static string GetPackageBuildId(byte[] package, string manifestName)
    {
        using var stream = new MemoryStream(package, writable: false);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        var entry = archive.Entries.SingleOrDefault(item => string.Equals(
            NormalizeZipEntry(item.FullName), manifestName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException(
                $"Package must contain {manifestName} at its root.");
        if (entry.Length is < 1 or > 4096)
        {
            throw new InvalidDataException($"Package manifest {manifestName} has an invalid size.");
        }
        using var document = JsonDocument.Parse(entry.Open());
        var buildId = document.RootElement.TryGetProperty("buildId", out var value)
            ? value.GetString()
            : null;
        return buildId is { Length: 32 }
               && buildId.All(character => char.IsAsciiHexDigit(character))
            ? buildId
            : throw new InvalidDataException($"Package manifest {manifestName} has an invalid buildId.");
    }

    private static string NormalizeZipEntry(string entryName)
    {
        var normalized = entryName.Replace('\\', '/').TrimStart('/');
        if (string.IsNullOrWhiteSpace(normalized)
            || normalized.Split('/').Any(segment => segment is "" or "." or ".."))
        {
            throw new InvalidDataException($"ZIP entry '{entryName}' has an unsafe path.");
        }
        return normalized;
    }

    private static string GetContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".html" => "text/html; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".js" or ".mjs" => "text/javascript; charset=utf-8",
        ".json" or ".map" => "application/json; charset=utf-8",
        ".svg" => "image/svg+xml",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".ico" => "image/x-icon",
        ".woff" => "font/woff",
        ".woff2" => "font/woff2",
        _ => "application/octet-stream"
    };

    private static JsonElement AddPublicationOutputs(JsonElement outputs, string frontendUrl)
    {
        var result = JsonNode.Parse(outputs.GetRawText())?.AsObject() ?? new JsonObject();
        result["frontendUrl"] = new JsonObject
        {
            ["type"] = "String",
            ["value"] = frontendUrl
        };
        result["applicationPublished"] = new JsonObject
        {
            ["type"] = "Bool",
            ["value"] = true
        };
        using var document = JsonDocument.Parse(result.ToJsonString());
        return document.RootElement.Clone();
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        Uri uri,
        JsonNode? body,
        CancellationToken ct)
    {
        var token = await _credential.GetTokenAsync(
            new TokenRequestContext(new[] { ArmScope }), ct);
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        if (body is not null)
        {
            request.Content = new StringContent(
                body.ToJsonString(), Encoding.UTF8, "application/json");
        }
        return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    private static async Task<JsonElement> ReadSuccessJsonAsync(
        HttpResponseMessage response,
        CancellationToken ct)
    {
        await EnsureSuccessAsync(response, ct);
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        return document.RootElement.Clone();
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(ct);
        if (body.Length > 4000)
        {
            body = body[..4000];
        }
        throw new InvalidOperationException(
            $"Azure Resource Manager returned {(int)response.StatusCode} {response.ReasonPhrase}: {body}");
    }

    private string DeploymentUri(string deploymentName)
        => $"https://management.azure.com/subscriptions/{_options.SubscriptionId}" +
           $"/providers/Microsoft.Resources/deployments/{Uri.EscapeDataString(deploymentName)}";

    private static JsonObject Parameter(string value) => new() { ["value"] = value };

    private static string GetOutputString(JsonElement outputs, string name)
    {
        if (outputs.TryGetProperty(name, out var output)
            && output.TryGetProperty("value", out var value)
            && value.GetString() is { Length: > 0 } text)
        {
            return text;
        }

        throw new InvalidOperationException($"ARM deployment output '{name}' is missing.");
    }

    private static string CreateDeploymentName(string projectSlug)
        => $"mnai-{projectSlug}-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}"[..64];

    private static JsonElement EmptyObject()
    {
        using var document = JsonDocument.Parse("{}");
        return document.RootElement.Clone();
    }

    private static string LoadTemplate()
    {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream(TemplateResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded ARM template '{TemplateResourceName}' was not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
