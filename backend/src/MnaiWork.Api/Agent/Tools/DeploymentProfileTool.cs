using System.Text.Json;
using MnaiWork.Api.Configuration;
using MnaiWork.Api.Infrastructure;

namespace MnaiWork.Api.Agent.Tools;

public sealed class GetDeploymentProfileTool : IAgentTool
{
    private readonly DeploymentProfileService _profiles;

    public GetDeploymentProfileTool(DeploymentProfileService profiles)
        => _profiles = profiles;

    public string Name => "get_deployment_profile";

    public string Description =>
        "Read the current Cosmos-backed software-factory deployment and build profile. " +
        "This tool is read-only; users edit the profile through the authenticated API.";

    public string ParametersSchema => """
    { "type": "object", "properties": {} }
    """;

    public Task<ToolResult> ExecuteAsync(
        JsonElement arguments,
        ToolContext context,
        CancellationToken ct)
        => Task.FromResult(ToolResult.Ok(
            JsonSerializer.Serialize(_profiles.Current, JsonDefaults.Options)));
}