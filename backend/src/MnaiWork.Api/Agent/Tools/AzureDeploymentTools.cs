using System.Security.Cryptography;
using System.Text.Json;
using MnaiWork.Api.Data;
using MnaiWork.Api.Deployment;
using MnaiWork.Api.Infrastructure;
using MnaiWork.Api.Models;
using MnaiWork.Api.Storage;

namespace MnaiWork.Api.Agent.Tools;

public sealed class PreviewAzureProjectTool : IAgentTool
{
    private readonly ArmProjectDeploymentClient _deployments;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IFileStorage _storage;
    private readonly ILogger<PreviewAzureProjectTool> _logger;

    public PreviewAzureProjectTool(
        ArmProjectDeploymentClient deployments,
        IServiceScopeFactory scopeFactory,
        IFileStorage storage,
        ILogger<PreviewAzureProjectTool> logger)
    {
        _deployments = deployments;
        _scopeFactory = scopeFactory;
        _storage = storage;
        _logger = logger;
    }

    public string Name => "preview_azure_project";

    public string Description =>
        "Preview the fixed Azure deployment using ARM what-if and bind it to tested backend and " +
        "frontend ZIP files from this conversation. Requires exact user UI approval after the " +
        "matching E2B desktop and mobile screenshots. " +
        "The target tenant, subscription, resource group, shared App Service Plan, and allowed " +
        "resource types are fixed by the server. This tool never deploys resources.";

    public string ParametersSchema => AzureDeploymentToolSchemas.Preview;

    public async Task<ToolResult> ExecuteAsync(
        JsonElement arguments,
        ToolContext context,
        CancellationToken ct)
    {
        var request = Deserialize(arguments);
        if (request is null)
        {
            return ToolResult.Fail("preview_azure_project requires valid project deployment arguments.");
        }

        var accessError = await AzureDeploymentToolAccess.ValidateAsync(
            _scopeFactory, _deployments, context, ct);
        if (accessError is not null)
        {
            return ToolResult.Fail(accessError);
        }

        var (packages, packageError) = await DeploymentPackages.LoadAsync(
            _scopeFactory, _storage, request, context, UiApprovalMode.RequireLatestUserMessage, ct);
        if (packages is null)
        {
            return ToolResult.Fail(packageError!);
        }

        try
        {
            _deployments.ValidateBackendPackageForTarget(packages.Backend);
            var result = await _deployments.WhatIfAsync(
                request, packages.BackendHash, packages.FrontendHash, ct);
            var json = JsonSerializer.Serialize(result.Changes, JsonDefaults.Options);
            if (json.Length > 12_000)
            {
                json = json[..12_000] + "... [what-if output truncated]";
            }

            var approvalPhrase = AzureDeploymentToolAccess.ApprovalPhrase(request.ProjectSlug);
            return ToolResult.Ok(
                $"Azure what-if succeeded. ProjectSlug: {request.ProjectSlug}. " +
                $"CosmosDatabaseName: {request.CosmosDatabaseName}. " +
                $"CosmosContainerName: {request.CosmosContainerName}. " +
                $"DeploymentFingerprint: {result.DeploymentFingerprint}. " +
                "No resources were deployed. Review this result with the user:\n" + json +
                $"\nTo approve deployment, the user must send exactly: {approvalPhrase}");
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or InvalidDataException or TimeoutException)
        {
            _logger.LogError(ex, "Azure what-if failed for project {ProjectSlug}.", request.ProjectSlug);
            return ToolResult.Fail($"Azure what-if failed: {ex.Message}");
        }
    }

    private static AzureProjectDeploymentRequest? Deserialize(JsonElement arguments)
    {
        try
        {
            return arguments.Deserialize<AzureProjectDeploymentRequest>(JsonDefaults.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

public sealed class DeployAzureProjectTool : IAgentTool
{
    private readonly ArmProjectDeploymentClient _deployments;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IFileStorage _storage;
    private readonly ILogger<DeployAzureProjectTool> _logger;

    public DeployAzureProjectTool(
        ArmProjectDeploymentClient deployments,
        IServiceScopeFactory scopeFactory,
        IFileStorage storage,
        ILogger<DeployAzureProjectTool> logger)
    {
        _deployments = deployments;
        _scopeFactory = scopeFactory;
        _storage = storage;
        _logger = logger;
    }

    public string Name => "deploy_azure_project";

    public string Description =>
        "Deploy a previously previewed generated project. The tool succeeds only when the latest " +
        "user message is the exact approval phrase returned by preview_azure_project. It creates " +
        "or updates only App Service API, Storage Account, Cosmos DB for NoSQL, and Key Vault, " +
        "publishes the approved ZIP files, and verifies their endpoints.";

    public string ParametersSchema => AzureDeploymentToolSchemas.Deploy;

    public async Task<ToolResult> ExecuteAsync(
        JsonElement arguments,
        ToolContext context,
        CancellationToken ct)
    {
        AzureProjectDeploymentRequest? request;
        try
        {
            request = arguments.Deserialize<AzureProjectDeploymentRequest>(JsonDefaults.Options);
        }
        catch (JsonException)
        {
            request = null;
        }

        if (request is null)
        {
            return ToolResult.Fail("deploy_azure_project requires valid project deployment arguments.");
        }

        var accessError = await AzureDeploymentToolAccess.ValidateAsync(
            _scopeFactory, _deployments, context, ct);
        if (accessError is not null)
        {
            return ToolResult.Fail(accessError);
        }

        var (packages, packageError) = await DeploymentPackages.LoadAsync(
            _scopeFactory, _storage, request, context, UiApprovalMode.CoveredByMatchingPreview, ct);
        if (packages is null)
        {
            return ToolResult.Fail(packageError!);
        }

        var fingerprint = _deployments.GetDeploymentFingerprint(
            request, packages.BackendHash, packages.FrontendHash);
        var approvalError = await ValidateApprovalAsync(request, fingerprint, context, ct);
        if (approvalError is not null)
        {
            return ToolResult.Fail(approvalError);
        }

        try
        {
            var result = await _deployments.DeployAsync(
                request, packages.Backend, packages.Frontend, fingerprint, ct);
            var buildId = ArmProjectDeploymentClient.GetPackageBuildId(
                packages.Backend, "deployment-manifest.json");
            var recordBytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schemaVersion = 1,
                projectSlug = request.ProjectSlug,
                deploymentName = result.DeploymentName,
                provisioningState = result.ProvisioningState,
                subscriptionId = result.SubscriptionId,
                resourceGroup = result.ResourceGroup,
                deployedAt = DateTimeOffset.UtcNow,
                backendPackageSha256 = packages.BackendHash,
                frontendPackageSha256 = packages.FrontendHash,
                backendPackageFileId = request.BackendPackageFileId,
                frontendPackageFileId = request.FrontendPackageFileId,
                buildId,
                cosmosDatabaseName = request.CosmosDatabaseName,
                cosmosContainerName = request.CosmosContainerName,
                healthCheckPath = request.HealthCheckPath,
                outputs = result.Outputs
            }, JsonDefaults.Options);
            var record = await _storage.UploadAsync(
                new ArtifactOwner(context.UserId, context.ThreadId),
                $"{request.ProjectSlug}-deployment-record.json",
                ArtifactKind.DeploymentRecord,
                recordBytes,
                "application/json; charset=utf-8",
                ct);
            return ToolResult.Ok(
                $"Azure infrastructure deployment and application publication completed. " +
                $"ProjectSlug: {request.ProjectSlug}. " +
                $"Deployment: {result.DeploymentName}. State: {result.ProvisioningState}. " +
                $"DeploymentRecordFileId: {record.Id}. " +
                $"Outputs: {JsonSerializer.Serialize(result.Outputs, JsonDefaults.Options)}",
                record);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or TimeoutException)
        {
            _logger.LogError(ex, "Azure deployment failed for project {ProjectSlug}.", request.ProjectSlug);
            return ToolResult.Fail($"Azure deployment failed: {ex.Message}");
        }
    }

    private async Task<string?> ValidateApprovalAsync(
        AzureProjectDeploymentRequest request,
        string fingerprint,
        ToolContext context,
        CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var messages = scope.ServiceProvider.GetRequiredService<IMessageRepository>();
        var history = await messages.ListAsync(context.ThreadId, ct);
        var latestUserMessage = history.LastOrDefault(message => message.Role == MessageRole.User);
        var expected = AzureDeploymentToolAccess.ApprovalPhrase(request.ProjectSlug);

        if (latestUserMessage is null
            || !string.Equals(latestUserMessage.Content.Trim(), expected, StringComparison.Ordinal))
        {
            return $"Deployment is not approved. The latest user message must be exactly: {expected}";
        }

        var latestPreview = history.LastOrDefault(message =>
            message.Role == MessageRole.Tool
            && string.Equals(message.ToolName, "preview_azure_project", StringComparison.OrdinalIgnoreCase)
            && message.Sequence < latestUserMessage.Sequence);

        var hasMatchingPreview = latestPreview is not null
            && latestPreview.Content.Contains(
                $"ProjectSlug: {request.ProjectSlug}.", StringComparison.OrdinalIgnoreCase)
            && latestPreview.Content.Contains(
                $"CosmosDatabaseName: {request.CosmosDatabaseName}.", StringComparison.OrdinalIgnoreCase)
            && latestPreview.Content.Contains(
                $"CosmosContainerName: {request.CosmosContainerName}.", StringComparison.OrdinalIgnoreCase);

        hasMatchingPreview = hasMatchingPreview
            && latestPreview!.Content.Contains(
                $"DeploymentFingerprint: {fingerprint}.",
                StringComparison.Ordinal);

        return hasMatchingPreview
            ? null
            : "Deployment arguments do not match the most recent successful what-if preview.";
    }
}

public sealed class ListAzureProjectResourcesTool : IAgentTool
{
    private readonly ArmProjectDeploymentClient _deployments;
    private readonly IServiceScopeFactory _scopeFactory;

    public ListAzureProjectResourcesTool(
        ArmProjectDeploymentClient deployments,
        IServiceScopeFactory scopeFactory)
    {
        _deployments = deployments;
        _scopeFactory = scopeFactory;
    }

    public string Name => "list_azure_project_resources";

    public string Description =>
        "List safe summaries of supported resources in the Cosmos profile Generated Resource " +
        "Group. Subscription and resource group cannot be supplied by the model. This is read-only.";

    public string ParametersSchema => """
    { "type": "object", "properties": {} }
    """;

    public async Task<ToolResult> ExecuteAsync(
        JsonElement arguments,
        ToolContext context,
        CancellationToken ct)
    {
        var accessError = await AzureDeploymentToolAccess.ValidateAsync(
            _scopeFactory, _deployments, context, ct);
        if (accessError is not null)
        {
            return ToolResult.Fail(accessError);
        }

        try
        {
            var result = await _deployments.ListGeneratedResourcesAsync(ct);
            return ToolResult.Ok(JsonSerializer.Serialize(result, JsonDefaults.Options));
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
        {
            return ToolResult.Fail($"Unable to list Azure resources: {ex.Message}");
        }
    }
}

public sealed class GetAzureProjectResourceTool : IAgentTool
{
    private readonly ArmProjectDeploymentClient _deployments;
    private readonly IServiceScopeFactory _scopeFactory;

    public GetAzureProjectResourceTool(
        ArmProjectDeploymentClient deployments,
        IServiceScopeFactory scopeFactory)
    {
        _deployments = deployments;
        _scopeFactory = scopeFactory;
    }

    public string Name => "get_azure_project_resource";

    public string Description =>
        "Get read-only ARM details for one supported resource in the configured Generated Resource " +
        "Group. Use a resourceId returned by list_azure_project_resources. Secret-like fields are redacted.";

    public string ParametersSchema => """
    {
      "type": "object",
      "properties": {
        "resourceId": {
          "type": "string",
          "description": "Exact resourceId returned by list_azure_project_resources."
        }
      },
      "required": ["resourceId"]
    }
    """;

    public async Task<ToolResult> ExecuteAsync(
        JsonElement arguments,
        ToolContext context,
        CancellationToken ct)
    {
        var resourceId = arguments.TryGetProperty("resourceId", out var idElement)
            ? idElement.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(resourceId))
        {
            return ToolResult.Fail("resourceId is required.");
        }

        var accessError = await AzureDeploymentToolAccess.ValidateAsync(
            _scopeFactory, _deployments, context, ct);
        if (accessError is not null)
        {
            return ToolResult.Fail(accessError);
        }

        try
        {
            var result = await _deployments.GetGeneratedResourceAsync(resourceId, ct);
            var json = JsonSerializer.Serialize(result, JsonDefaults.Options);
            return ToolResult.Ok(json.Length > 30_000 ? json[..30_000] + "... [truncated]" : json);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or TimeoutException)
        {
            return ToolResult.Fail($"Unable to read Azure resource: {ex.Message}");
        }
    }
}

internal static class AzureDeploymentToolAccess
{
    public static async Task<string?> ValidateAsync(
        IServiceScopeFactory scopeFactory,
        ArmProjectDeploymentClient deployments,
        ToolContext context,
        CancellationToken ct)
    {
        if (!deployments.Enabled)
        {
            return "Azure project provisioning is disabled by the Cosmos deployment profile.";
        }

        using var scope = scopeFactory.CreateScope();
        var threads = scope.ServiceProvider.GetRequiredService<IThreadRepository>();
        var users = scope.ServiceProvider.GetRequiredService<IUserRepository>();
        if (await threads.GetAsync(context.UserId, context.ThreadId, ct) is null)
        {
            return "The current user does not own this conversation.";
        }

        var user = await users.GetAsync(context.UserId, ct);
        if (user is null
            || !string.Equals(user.TenantId, deployments.TenantId, StringComparison.OrdinalIgnoreCase))
        {
            return "Azure deployment is restricted to users in the configured demo tenant.";
        }

        return null;
    }

    public static string ApprovalPhrase(string projectSlug) => $"DEPLOY {projectSlug}";
}

internal sealed record DeploymentPackageSet(
    byte[] Backend,
    byte[] Frontend,
    string BackendHash,
    string FrontendHash);

internal enum UiApprovalMode
{
    RequireLatestUserMessage,
    CoveredByMatchingPreview
}

internal static class DeploymentPackages
{
    public static async Task<(DeploymentPackageSet? Packages, string? Error)> LoadAsync(
        IServiceScopeFactory scopeFactory,
        IFileStorage storage,
        AzureProjectDeploymentRequest request,
        ToolContext context,
        UiApprovalMode uiApprovalMode,
        CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var messages = scope.ServiceProvider.GetRequiredService<IMessageRepository>();
        var history = await messages.ListAsync(context.ThreadId, ct);

        var buildMessage = history.LastOrDefault(message =>
            message.Role == MessageRole.Tool
            && string.Equals(message.ToolName, "build_test_project", StringComparison.OrdinalIgnoreCase)
            && message.Artifacts.Any(artifact =>
                artifact.Kind == ArtifactKind.BackendPackage
                && string.Equals(artifact.Id, request.BackendPackageFileId, StringComparison.OrdinalIgnoreCase))
            && message.Artifacts.Any(artifact =>
                artifact.Kind == ArtifactKind.FrontendPackage
                && string.Equals(artifact.Id, request.FrontendPackageFileId, StringComparison.OrdinalIgnoreCase)));
        if (buildMessage is null)
        {
            return (null,
                "Backend and frontend packages must come from the same successful build_test_project call.");
        }
        var latestBuild = history.LastOrDefault(message =>
            message.Role == MessageRole.Tool
            && string.Equals(message.ToolName, "build_test_project", StringComparison.OrdinalIgnoreCase)
            && message.Artifacts.Any(artifact =>
                artifact.Kind == ArtifactKind.BackendPackage
                && string.Equals(
                    artifact.FileName,
                    $"{request.ProjectSlug}-backend.zip",
                    StringComparison.Ordinal)));
        if (latestBuild?.Id != buildMessage.Id)
        {
            return (null,
                "Deployment packages must come from the latest successful build for this project slug.");
        }
        if (uiApprovalMode == UiApprovalMode.RequireLatestUserMessage)
        {
            var uiApprovalError = SoftwareFactoryApprovals.ValidateUi(history, buildMessage);
            if (uiApprovalError is not null)
            {
                return (null, uiApprovalError);
            }
        }

        var backend = await ReadPackageAsync(
            buildMessage.Artifacts, storage, request.BackendPackageFileId,
            ArtifactKind.BackendPackage, "backend", ct);
        if (backend.Bytes is null)
        {
            return (null, backend.Error);
        }

        var frontend = await ReadPackageAsync(
            buildMessage.Artifacts, storage, request.FrontendPackageFileId,
            ArtifactKind.FrontendPackage, "frontend", ct);
        if (frontend.Bytes is null)
        {
            return (null, frontend.Error);
        }

        return (new DeploymentPackageSet(
            backend.Bytes,
            frontend.Bytes,
            Hash(backend.Bytes),
            Hash(frontend.Bytes)), null);
    }

    private static async Task<(byte[]? Bytes, string? Error)> ReadPackageAsync(
        IReadOnlyList<Artifact> artifacts,
        IFileStorage storage,
        string fileId,
        ArtifactKind expectedKind,
        string packageName,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(fileId))
        {
            return (null, $"The {packageName}PackageFileId is required.");
        }

        var artifact = artifacts
            .FirstOrDefault(file => string.Equals(file.Id, fileId, StringComparison.OrdinalIgnoreCase));
        if (artifact is null || artifact.Kind != expectedKind)
        {
            return (null,
                $"The {packageName} package must be a successful E2B build artifact in this conversation.");
        }
        var fileName = artifact.FileName;
        var blobPath = artifact.BlobPath;
        if (!string.Equals(Path.GetExtension(fileName), ".zip", StringComparison.OrdinalIgnoreCase))
        {
            return (null, $"The {packageName} package must be a ZIP file.");
        }

        var bytes = await storage.ReadBytesAsync(blobPath, ct);
        return bytes is null
            ? (null, $"The {packageName} package no longer exists in storage.")
            : (bytes, null);
    }

    private static string Hash(byte[] content)
        => Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
}

internal static class AzureDeploymentToolSchemas
{
    public const string Preview = """
    {
      "type": "object",
      "properties": {
        "projectSlug": {
          "type": "string",
          "description": "3-24 lowercase letters, numbers, or hyphens; must start and end alphanumeric."
        },
        "cosmosDatabaseName": { "type": "string", "default": "app" },
        "cosmosContainerName": { "type": "string", "default": "items" }
                ,"backendPackageFileId": {
                    "type": "string",
                    "description": "backendPackageFileId returned by a successful build_test_project call."
                }
                ,"frontendPackageFileId": {
                    "type": "string",
                    "description": "frontendPackageFileId returned by the same successful build_test_project call."
                }
                ,"healthCheckPath": { "type": "string", "default": "/health" }
      },
            "required": ["projectSlug", "backendPackageFileId", "frontendPackageFileId"]
    }
    """;

    public const string Deploy = Preview;
}
