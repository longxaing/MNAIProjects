using System.Text.Json.Serialization;

namespace MnaiWork.Api.Models;

public enum MessageRole
{
    User,
    Assistant,
    Tool,
    System
}

public enum RunStatus
{
    Queued,
    Running,
    Completed,
    Failed,
    Canceled
}

public enum ArtifactKind
{
    Docx,
    Pptx,
    SourceZip,
    BackendPackage,
    FrontendPackage,
    BuildReport,
    UiScreenshot,
    DeploymentRecord
}

/// <summary>Kind of a user-uploaded file.</summary>
public enum AttachmentKind
{
    Image,
    Pdf,
    Docx,
    Pptx,
    Other
}

/// <summary>A user-uploaded file stored in Blob Storage and referenced from a message.</summary>
public sealed class Attachment
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    [JsonPropertyName("kind")]
    public AttachmentKind Kind { get; set; }

    [JsonPropertyName("fileName")]
    public string FileName { get; set; } = string.Empty;

    [JsonPropertyName("blobPath")]
    public string BlobPath { get; set; } = string.Empty;

    [JsonPropertyName("contentType")]
    public string ContentType { get; set; } = "application/octet-stream";

    [JsonPropertyName("sizeBytes")]
    public long SizeBytes { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Per-user quota / throttling rules (inspired by Societas' throttle_rule).</summary>
public sealed class UserQuota
{
    /// <summary>Max agent runs a user may start per UTC day. 0 = unlimited.</summary>
    [JsonPropertyName("dailyRunLimit")]
    public int DailyRunLimit { get; set; }

    /// <summary>Max concurrently active runs. 0 = unlimited.</summary>
    [JsonPropertyName("maxConcurrentRuns")]
    public int MaxConcurrentRuns { get; set; }
}

/// <summary>
/// A user account. Partitioned by <see cref="Id"/> (the stable Entra ID object id / SUID),
/// so a point-read by user id is a single-partition lookup. Created on first sign-in.
/// </summary>
public sealed class User
{
    /// <summary>Stable user id — the Entra ID <c>oid</c> (object id). Also the partition key.</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>Entra ID tenant id the user signed in from.</summary>
    [JsonPropertyName("tenantId")]
    public string? TenantId { get; set; }

    [JsonPropertyName("email")]
    public string? Email { get; set; }

    [JsonPropertyName("displayName")]
    public string? DisplayName { get; set; }

    /// <summary>True for personal Microsoft accounts (MSA), false for organizational (AAD).</summary>
    [JsonPropertyName("personalAccount")]
    public bool PersonalAccount { get; set; }

    [JsonPropertyName("quota")]
    public UserQuota Quota { get; set; } = new();

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("lastSeenAt")]
    public DateTimeOffset LastSeenAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Editable software-factory runtime configuration stored as one Cosmos item.</summary>
public sealed class DeploymentProfile
{
    public const string DefaultId = "default";

    [JsonPropertyName("id")]
    public string Id { get; set; } = DefaultId;

    [JsonPropertyName("azureProvisioningEnabled")]
    public bool AzureProvisioningEnabled { get; set; }

    [JsonPropertyName("tenantId")]
    public string TenantId { get; set; } = string.Empty;

    [JsonPropertyName("subscriptionId")]
    public string SubscriptionId { get; set; } = string.Empty;

    [JsonPropertyName("generatedResourceGroup")]
    public string GeneratedResourceGroup { get; set; } = "rg-mnaiwork-generated-demo";

    [JsonPropertyName("location")]
    public string Location { get; set; } = "canadacentral";

    [JsonPropertyName("cosmosLocation")]
    public string CosmosLocation { get; set; } = string.Empty;

    [JsonPropertyName("appServicePlanName")]
    public string AppServicePlanName { get; set; } = "asp-mnaiwork-generated-demo";

    [JsonPropertyName("existingAppServicePlanResourceId")]
    public string ExistingAppServicePlanResourceId { get; set; } = string.Empty;

    [JsonPropertyName("appServicePlanOs")]
    public string AppServicePlanOs { get; set; } = "Windows";

    [JsonPropertyName("deploymentPrincipalId")]
    public string DeploymentPrincipalId { get; set; } = string.Empty;

    [JsonPropertyName("azureTimeoutMinutes")]
    public int AzureTimeoutMinutes { get; set; } = 30;

    [JsonPropertyName("buildExecutionEnabled")]
    public bool BuildExecutionEnabled { get; set; } = true;

    [JsonPropertyName("maxConcurrentBuilds")]
    public int MaxConcurrentBuilds { get; set; } = 1;

    [JsonPropertyName("commandTimeoutMinutes")]
    public int CommandTimeoutMinutes { get; set; } = 15;

    [JsonPropertyName("totalTimeoutMinutes")]
    public int TotalTimeoutMinutes { get; set; } = 45;

    [JsonPropertyName("playwrightVersion")]
    public string PlaywrightVersion { get; set; } = "1.62.1";

    [JsonPropertyName("azureAdTenantId")]
    public string AzureAdTenantId { get; set; } = string.Empty;

    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    [JsonPropertyName("updatedBy")]
    public string UpdatedBy { get; set; } = "system";

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("_etag")]
    public string? ETag { get; set; }

    public IReadOnlyDictionary<string, string?> ToConfiguration() =>
        new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["AzureProvisioning:Enabled"] = AzureProvisioningEnabled.ToString(),
            ["AzureProvisioning:TenantId"] = TenantId,
            ["AzureProvisioning:SubscriptionId"] = SubscriptionId,
            ["AzureProvisioning:GeneratedResourceGroup"] = GeneratedResourceGroup,
            ["AzureProvisioning:Location"] = Location,
            ["AzureProvisioning:CosmosLocation"] = CosmosLocation,
            ["AzureProvisioning:AppServicePlanName"] = AppServicePlanName,
            ["AzureProvisioning:ExistingAppServicePlanResourceId"] = ExistingAppServicePlanResourceId,
            ["AzureProvisioning:AppServicePlanOs"] = AppServicePlanOs,
            ["AzureProvisioning:DeploymentPrincipalId"] = DeploymentPrincipalId,
            ["AzureProvisioning:TimeoutMinutes"] = AzureTimeoutMinutes.ToString(),
            ["BuildExecution:Enabled"] = BuildExecutionEnabled.ToString(),
            ["BuildExecution:MaxConcurrentBuilds"] = MaxConcurrentBuilds.ToString(),
            ["BuildExecution:CommandTimeoutMinutes"] = CommandTimeoutMinutes.ToString(),
            ["BuildExecution:TotalTimeoutMinutes"] = TotalTimeoutMinutes.ToString(),
            ["BuildExecution:PlaywrightVersion"] = PlaywrightVersion,
            ["AzureAd:TenantId"] = AzureAdTenantId
        };
}

/// <summary>A conversation. Partitioned by <see cref="UserId"/>.</summary>
public sealed class ChatThread
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    [JsonPropertyName("userId")]
    public string UserId { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = "New conversation";

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>A reference to a generated file stored in Blob Storage.</summary>
public sealed class Artifact
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    [JsonPropertyName("kind")]
    public ArtifactKind Kind { get; set; }

    [JsonPropertyName("fileName")]
    public string FileName { get; set; } = string.Empty;

    [JsonPropertyName("blobPath")]
    public string BlobPath { get; set; } = string.Empty;

    [JsonPropertyName("sizeBytes")]
    public long SizeBytes { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>A single message in a thread. Partitioned by <see cref="ThreadId"/>.</summary>
public sealed class ChatMessage
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    [JsonPropertyName("threadId")]
    public string ThreadId { get; set; } = string.Empty;

    [JsonPropertyName("runId")]
    public string? RunId { get; set; }

    [JsonPropertyName("role")]
    public MessageRole Role { get; set; }

    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;

    /// <summary>Tool name when <see cref="Role"/> is Tool.</summary>
    [JsonPropertyName("toolName")]
    public string? ToolName { get; set; }

    [JsonPropertyName("toolArguments")]
    public System.Text.Json.JsonElement? ToolArguments { get; set; }

    [JsonPropertyName("toolSucceeded")]
    public bool? ToolSucceeded { get; set; }

    /// <summary>Monotonic ordering key within a thread.</summary>
    [JsonPropertyName("sequence")]
    public long Sequence { get; set; }

    [JsonPropertyName("artifacts")]
    public List<Artifact> Artifacts { get; set; } = new();

    /// <summary>Files the user attached to this message (images / pdf / docx / pptx).</summary>
    [JsonPropertyName("attachments")]
    public List<Attachment> Attachments { get; set; } = new();

    /// <summary>True while the assistant message is still streaming.</summary>
    [JsonPropertyName("streaming")]
    public bool Streaming { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>An agent execution. Partitioned by <see cref="ThreadId"/>.</summary>
public sealed class AgentRun
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    [JsonPropertyName("threadId")]
    public string ThreadId { get; set; } = string.Empty;

    [JsonPropertyName("userId")]
    public string UserId { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public RunStatus Status { get; set; } = RunStatus.Queued;

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
