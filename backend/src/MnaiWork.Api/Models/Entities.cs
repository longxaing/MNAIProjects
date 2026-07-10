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

    [JsonPropertyName("downloadUrl")]
    public string DownloadUrl { get; set; } = string.Empty;

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
