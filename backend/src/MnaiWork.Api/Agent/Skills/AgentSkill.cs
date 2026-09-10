using System.Collections.Concurrent;
using System.Reflection;

namespace MnaiWork.Api.Agent.Skills;

public interface IAgentSkill
{
    string Name { get; }
    string Description { get; }
    string Version { get; }
    string Category { get; }
    IReadOnlySet<string> ToolNames { get; }
    string LoadInstructions();
}

public sealed class SoftwareFactorySkill : IAgentSkill
{
    private const string SkillResourceName =
        "MnaiWork.Api.Agent.Skills.skill_packs.software_factory.SKILL.md";

    public string Name => "software-factory";

    public string Description =>
        "Create, test, and deploy React plus ASP.NET Core demo projects using a mandatory " +
        "code, test, Azure what-if, approval, and deployment workflow.";

    public string Version => "1.2.7";
    public string Category => "engineering";

    public IReadOnlySet<string> ToolNames { get; } = new HashSet<string>(
        new[]
        {
            "create_project_workspace",
            "update_project_workspace",
            "read_project_workspace",
            "build_test_project",
            "preview_azure_project",
            "deploy_azure_project",
            "list_azure_project_resources",
            "get_azure_project_resource",
            "get_deployment_profile"
        },
        StringComparer.OrdinalIgnoreCase);

    public string LoadInstructions()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(SkillResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded skill resource '{SkillResourceName}' was not found.");
        using var reader = new StreamReader(stream);
        return StripFrontmatter(reader.ReadToEnd());
    }

    private static string StripFrontmatter(string content)
    {
        if (!content.StartsWith("---", StringComparison.Ordinal))
        {
            return content.Trim();
        }

        var end = content.IndexOf("\n---", 3, StringComparison.Ordinal);
        return end < 0 ? content.Trim() : content[(end + 4)..].Trim();
    }
}

public sealed class AgentSkillRegistry
{
    private readonly Dictionary<string, IAgentSkill> _skills;
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _loadedByRun = new();

    public AgentSkillRegistry(IEnumerable<IAgentSkill> skills)
    {
        _skills = skills.ToDictionary(skill => skill.Name, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyCollection<IAgentSkill> All => _skills.Values;

    public bool TryLoad(string runId, string skillName, out IAgentSkill skill)
    {
        if (!_skills.TryGetValue(skillName, out skill!))
        {
            return false;
        }

        var loaded = _loadedByRun.GetOrAdd(
            runId,
            _ => new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase));
        loaded[skill.Name] = 0;
        return true;
    }

    public bool IsToolAvailable(string runId, string toolName)
    {
        var owner = _skills.Values.FirstOrDefault(skill => skill.ToolNames.Contains(toolName));
        if (owner is null)
        {
            return true;
        }

        return _loadedByRun.TryGetValue(runId, out var loaded)
            && loaded.ContainsKey(owner.Name);
    }

    public string BuildCatalogPrompt()
    {
        if (_skills.Count == 0)
        {
            return string.Empty;
        }

        var catalog = string.Join(
            "\n",
            _skills.Values
                .OrderBy(skill => skill.Name)
                .Select(skill => $"- `{skill.Name}`: {skill.Description}"));

        return $$"""
        ## Server-side skills
        Skills are specialized server-side workflow packs. Calling a matching skill is mandatory, not
        optional. For any request to create, modify, test, or deploy an application, website, frontend,
        backend, API, or software project, the first action MUST be `load_skill` with `skillName` set to
        `software-factory`. Do not answer from generic software knowledge before loading it. Follow the
        loaded workflow exactly. A skill coordinates tools; it does not make unavailable tools real.

        Available skills:
        {{catalog}}
        """;
    }

    public void ClearRun(string runId) => _loadedByRun.TryRemove(runId, out _);
}
