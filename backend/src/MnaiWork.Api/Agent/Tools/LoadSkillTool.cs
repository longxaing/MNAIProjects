using System.Text.Json;
using MnaiWork.Api.Agent.Skills;
using MnaiWork.Api.Infrastructure;

namespace MnaiWork.Api.Agent.Tools;

public sealed class LoadSkillTool : IAgentTool
{
    private readonly AgentSkillRegistry _skills;
    private readonly ILogger<LoadSkillTool> _logger;

    public LoadSkillTool(AgentSkillRegistry skills, ILogger<LoadSkillTool> logger)
    {
        _skills = skills;
        _logger = logger;
    }

    public string Name => "load_skill";

    public string Description =>
        "Load a server-side skill workflow into the current agent run. This must be the first action " +
        "for any request to create, modify, test, or deploy an application, website, frontend, " +
        "backend, API, or software project; use skillName 'software-factory'.";

    public string ParametersSchema => """
    {
      "type": "object",
      "properties": {
        "skillName": {
          "type": "string",
          "description": "Exact skill name from the server-side skill catalog."
        }
      },
      "required": ["skillName"]
    }
    """;

    public Task<ToolResult> ExecuteAsync(
        JsonElement arguments,
        ToolContext context,
        CancellationToken ct)
    {
        string? skillName;
        try
        {
            skillName = arguments.Deserialize<LoadSkillArguments>(JsonDefaults.Options)?.SkillName;
        }
        catch (JsonException)
        {
            skillName = null;
        }

        if (string.IsNullOrWhiteSpace(skillName))
        {
            return Task.FromResult(ToolResult.Fail("load_skill requires a non-empty skillName."));
        }

        if (!_skills.TryLoad(context.RunId, skillName.Trim(), out var skill))
        {
            var available = string.Join(", ", _skills.All.Select(item => item.Name).OrderBy(name => name));
            return Task.FromResult(ToolResult.Fail(
                $"Skill '{skillName}' was not found. Available skills: {available}"));
        }

        try
        {
            var instructions = skill.LoadInstructions();
            _logger.LogInformation(
                "Server-side skill loaded: {SkillName} v{Version} for run {RunId}.",
                skill.Name, skill.Version, context.RunId);
            return Task.FromResult(ToolResult.Ok(
                $"Skill '{skill.Name}' loaded. Follow these instructions for this run:\n\n{instructions}"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load server-side skill {SkillName}.", skill.Name);
            return Task.FromResult(ToolResult.Fail($"Failed to load skill '{skill.Name}'."));
        }
    }

    private sealed class LoadSkillArguments
    {
        public string SkillName { get; set; } = string.Empty;
    }
}
