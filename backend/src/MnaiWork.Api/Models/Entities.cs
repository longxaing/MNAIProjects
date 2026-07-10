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
    Pptx
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

    /// <summary>Monotonic ordering key within a thread.</summary>
    [JsonPropertyName("sequence")]
    public long Sequence { get; set; }

    [JsonPropertyName("artifacts")]
    public List<Artifact> Artifacts { get; set; } = new();

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
