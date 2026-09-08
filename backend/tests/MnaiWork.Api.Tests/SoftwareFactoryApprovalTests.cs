using MnaiWork.Api.Agent;
using MnaiWork.Api.Agent.Skills;
using MnaiWork.Api.Agent.Tools;
using MnaiWork.Api.Models;
using MnaiWork.BuildExecution;
using Xunit;

namespace MnaiWork.Api.Tests;

public sealed class SoftwareFactoryApprovalTests
{
    [Fact]
    public void ValidateArchitecture_KeepsExactApprovalValidUntilArchitectureChanges()
    {
        var history = new[]
        {
            Message(MessageRole.Assistant, 1, "```mermaid\nflowchart LR\nA-->B\n```"),
            Message(MessageRole.User, 2, SoftwareFactoryApprovals.ArchitecturePhrase),
            Message(MessageRole.User, 3, "没有额外需求")
        };

        Assert.Null(SoftwareFactoryApprovals.ValidateArchitecture(history));
        Assert.NotNull(SoftwareFactoryApprovals.ValidateArchitecture(history[..1]));
        Assert.NotNull(SoftwareFactoryApprovals.ValidateArchitecture(history.Append(Message(
            MessageRole.Assistant,
            4,
            "```mermaid\nflowchart LR\nA-->C\n```")).ToArray()));
    }

    [Fact]
    public void ValidateSourceChangeAuthorization_KeepsLatestApprovedArchitectureValid()
    {
        var history = new[]
        {
            Message(MessageRole.Assistant, 1, "```mermaid\nflowchart LR\nA-->B\n```"),
            Message(MessageRole.User, 2, SoftwareFactoryApprovals.ArchitecturePhrase),
            Message(MessageRole.Tool, 3, "build passed"),
            Message(MessageRole.User, 4, "Make the UI more polished")
        };

        Assert.Null(SoftwareFactoryApprovals.ValidateSourceChangeAuthorization(
            history, "demo-one"));
        Assert.NotNull(SoftwareFactoryApprovals.ValidateSourceChangeAuthorization(
            history[..1], "demo-one"));

        var revisedArchitecture = history.Append(Message(
            MessageRole.Assistant,
            5,
            "```mermaid\nflowchart LR\nA-->C\n```")).ToArray();
        Assert.NotNull(SoftwareFactoryApprovals.ValidateSourceChangeAuthorization(
            revisedArchitecture, "demo-one"));
        Assert.Null(SoftwareFactoryApprovals.ValidateSourceChangeAuthorization(
            revisedArchitecture.Append(Message(
                MessageRole.User,
                6,
                SoftwareFactoryApprovals.ArchitecturePhrase)).ToArray(),
            "demo-one"));
    }

    [Fact]
    public void ValidateUi_RequiresTwoScreenshotsAndExactLaterApproval()
    {
        var build = Message(MessageRole.Tool, 3, "passed");
        build.ToolName = "build_test_project";
        build.Artifacts.AddRange(new[]
        {
            new Artifact { Kind = ArtifactKind.UiScreenshot },
            new Artifact { Kind = ArtifactKind.UiScreenshot }
        });
        var history = new[]
        {
            build,
            Message(MessageRole.User, 4, SoftwareFactoryApprovals.UiPhrase)
        };

        Assert.Null(SoftwareFactoryApprovals.ValidateUi(history, build));
        build.Artifacts.RemoveAt(0);
        Assert.NotNull(SoftwareFactoryApprovals.ValidateUi(history, build));
    }

    [Fact]
    public void BuildFailureDiagnostics_ReturnsStageAssertionAndApiErrorToAgent()
    {
        var response = new BuildProjectResponse(
            false,
            "Stage 'Playwright E2E' failed with exit code 1.",
            new[]
            {
                new BuildStepResult("dotnet tests", true, 0, 10, "Passed"),
                new BuildStepResult(
                    "Playwright E2E",
                    false,
                    1,
                    20,
                    "Locator: getByText('published')\nExpected: visible\n" +
                    "Generated API output:\nCosmosException: HTTP 500")
            },
            null,
            null);

        var diagnostic = BuildFailureDiagnostics.Create(response, "report-1");

        Assert.Contains("failedStage=Playwright E2E", diagnostic, StringComparison.Ordinal);
        Assert.Contains("passedStages=dotnet tests", diagnostic, StringComparison.Ordinal);
        Assert.Contains("Expected: visible", diagnostic, StringComparison.Ordinal);
        Assert.Contains("CosmosException: HTTP 500", diagnostic, StringComparison.Ordinal);
        Assert.Contains("Repair the latest SourceZip", diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void AgentRunWorkflow_ContinuesOnlyForRepairableBuildFailures()
    {
        var buildFailure = ToolResult.Fail(
            "tests failed",
            new[] { new Artifact { Kind = ArtifactKind.BuildReport } });
        var transportFailure = ToolResult.Fail("E2B unavailable");

        Assert.True(AgentRunWorkflow.RequiresCodeRepair(buildFailure));
        Assert.False(AgentRunWorkflow.RequiresCodeRepair(transportFailure));
        Assert.Contains("Do not stop", AgentRunWorkflow.RepairContinuationPrompt, StringComparison.Ordinal);
        Assert.Contains("build_test_project again", AgentRunWorkflow.RepairContinuationPrompt, StringComparison.Ordinal);
        Assert.Contains("Top-level Program is commonly global", AgentRunWorkflow.RepairContinuationPrompt, StringComparison.Ordinal);
        Assert.Equal(
            "Playwright E2E|expected visible",
            AgentRunWorkflow.GetFailureSignature(
                "failureSignature:\nPlaywright E2E|expected visible\n\nfailedStageDiagnostic:\nfull log"));
    }

    [Fact]
    public void ContextManager_FindsLatestMermaidArchitectureForPinnedContext()
    {
        var first = Message(MessageRole.Assistant, 1, "```mermaid\nA-->B\n```");
        var latest = Message(MessageRole.Assistant, 3, "```mermaid\nA-->C\n```");

        var result = ContextManager.FindLatestArchitecture(new[]
        {
            first,
            Message(MessageRole.User, 2, "change it"),
            latest
        });

        Assert.Same(latest, result);
    }

    [Fact]
    public void SoftwareFactorySkill_DescribesApprovedSameThreadIteration()
    {
        var skill = new SoftwareFactorySkill();
        var instructions = skill.LoadInstructions();

        Assert.Equal("1.2.0", skill.Version);
        Assert.Contains("Existing project iteration", instructions, StringComparison.Ordinal);
        Assert.Contains("pinned into later LLM", instructions, StringComparison.Ordinal);
        Assert.Contains("APPROVE ARCHITECTURE", instructions, StringComparison.Ordinal);
        Assert.Contains("APPROVE UI", instructions, StringComparison.Ordinal);
        Assert.Contains("thread-scoped", instructions, StringComparison.Ordinal);
    }

    [Fact]
    public void SoftwareFactoryRouting_RequiresSkillAndFixedStackBeforeGenericAdvice()
    {
        var registry = new AgentSkillRegistry(new[] { new SoftwareFactorySkill() });
        var catalog = registry.BuildCatalogPrompt();
        var instructions = new SoftwareFactorySkill().LoadInstructions();

        Assert.Contains("first action MUST be `load_skill`", catalog, StringComparison.Ordinal);
        Assert.Contains("React + TypeScript + Vite", SystemPrompts.Agent, StringComparison.Ordinal);
        Assert.Contains("ASP.NET Core", SystemPrompts.Agent, StringComparison.Ordinal);
        Assert.Contains("Never ask the user to choose", SystemPrompts.Agent, StringComparison.Ordinal);
        Assert.Contains("End immediately after the approval request", instructions, StringComparison.Ordinal);
        Assert.Contains("Production persistence is mandatory", instructions, StringComparison.Ordinal);
        Assert.Contains("CosmosClient", instructions, StringComparison.Ordinal);
        Assert.Contains("In-memory repositories", instructions, StringComparison.Ordinal);
        Assert.Contains("Never call", instructions, StringComparison.Ordinal);
        Assert.Contains("AddDefaultServices", instructions, StringComparison.Ordinal);
        Assert.Contains("There is no fixed three-cycle limit", instructions, StringComparison.Ordinal);
        Assert.Contains("failure signature", instructions, StringComparison.Ordinal);
        Assert.DoesNotContain("Stop after three repair cycles", instructions, StringComparison.Ordinal);
        Assert.Contains("browser-default form", instructions, StringComparison.Ordinal);
        Assert.Contains("actual primary workflow", instructions, StringComparison.Ordinal);
        Assert.Contains("horizontal overflow", instructions, StringComparison.Ordinal);
        Assert.Contains("Inspect both screenshots", instructions, StringComparison.Ordinal);
        Assert.Contains("template tests as placeholders", instructions, StringComparison.Ordinal);
        Assert.Contains("Generated App", instructions, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateLatestSourceRevision_RejectsStaleOrDifferentProjectRevision()
    {
        var oldRevision = new Artifact
        {
            Id = "old",
            Kind = ArtifactKind.SourceZip,
            FileName = "demo-one-source.zip"
        };
        var latestRevision = new Artifact
        {
            Id = "latest",
            Kind = ArtifactKind.SourceZip,
            FileName = "demo-one-source.zip"
        };
        var first = Message(MessageRole.Tool, 1, "created");
        first.Artifacts.Add(oldRevision);
        var second = Message(MessageRole.Tool, 2, "updated");
        second.Artifacts.Add(latestRevision);
        var history = new[] { first, second };

        Assert.Null(SoftwareFactoryApprovals.ValidateLatestSourceRevision(
            history, "demo-one", "latest"));
        Assert.Contains("stale", SoftwareFactoryApprovals.ValidateLatestSourceRevision(
            history, "demo-one", "old"), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("No source revision", SoftwareFactoryApprovals.ValidateLatestSourceRevision(
            history, "other-project", "latest"), StringComparison.Ordinal);
        Assert.Same(latestRevision, SoftwareFactoryApprovals.FindLatestSourceRevision(
            history, "demo-one"));
    }

    private static ChatMessage Message(MessageRole role, long sequence, string content) => new()
    {
        Role = role,
        Sequence = sequence,
        Content = content
    };
}