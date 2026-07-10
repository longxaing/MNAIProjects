using MnaiWork.Api.Models;

namespace MnaiWork.Api.Agent;

/// <summary>
/// A single server-sent event streamed to the client during a run.
/// A flat shape keeps the SSE payload trivial to consume in the browser.
/// </summary>
public sealed record AgentEvent
{
    /// <summary>message | delta | tool | artifact | message_done | run | error | done</summary>
    public required string Type { get; init; }

    public long Seq { get; init; }
    public string? MessageId { get; init; }
    public string? Role { get; init; }

    /// <summary>Incremental text for <c>delta</c> events.</summary>
    public string? Delta { get; init; }

    /// <summary>Full text for <c>message</c> / <c>message_done</c> events.</summary>
    public string? Content { get; init; }

    public string? Tool { get; init; }

    /// <summary>started | completed | failed</summary>
    public string? ToolStatus { get; init; }

    public string? Summary { get; init; }
    public Artifact? Artifact { get; init; }

    public string? RunId { get; init; }

    /// <summary>Run status string for <c>run</c> events.</summary>
    public string? Status { get; init; }

    public string? Error { get; init; }
}
