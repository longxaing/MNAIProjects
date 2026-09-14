using System.Text.Json;
using MnaiWork.Api.Models;

namespace MnaiWork.Api.Agent.Tools;

/// <summary>Identifiers available to a tool while it runs.</summary>
public sealed record ToolContext(string ThreadId, string UserId, string RunId);

/// <summary>Outcome of a tool invocation. <see cref="Output"/> is fed back to the model.</summary>
public sealed record ToolResult(bool Success, string Output, IReadOnlyList<Artifact> Artifacts)
{
    public static ToolResult Ok(string output, Artifact? artifact = null)
        => new(true, output, artifact is null ? Array.Empty<Artifact>() : new[] { artifact });

    public static ToolResult Ok(string output, IReadOnlyList<Artifact> artifacts)
        => new(true, output, artifacts);

    public static ToolResult Fail(string output) => new(false, output, Array.Empty<Artifact>());

    public static ToolResult Fail(string output, IReadOnlyList<Artifact> artifacts)
        => new(false, output, artifacts);
}

/// <summary>A capability the agent can invoke via function calling.</summary>
public interface IAgentTool
{
    string Name { get; }
    string Description { get; }

    /// <summary>JSON Schema (object) describing the tool arguments.</summary>
    string ParametersSchema { get; }

    Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context, CancellationToken ct);
}

/// <summary>Lookup for the registered tools.</summary>
public sealed class ToolRegistry
{
    private readonly Dictionary<string, IAgentTool> _tools;

    public ToolRegistry(IEnumerable<IAgentTool> tools)
        => _tools = tools.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<IAgentTool> All => _tools.Values;

    public bool TryGet(string name, out IAgentTool tool) => _tools.TryGetValue(name, out tool!);
}
