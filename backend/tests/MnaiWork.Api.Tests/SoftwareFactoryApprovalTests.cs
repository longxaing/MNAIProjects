using MnaiWork.Api.Agent;
using MnaiWork.Api.Agent.Skills;
using MnaiWork.Api.Agent.Tools;
using MnaiWork.Api.Models;
using Xunit;

namespace MnaiWork.Api.Tests;

public sealed class SoftwareFactoryApprovalTests
{
    [Fact]
    public void ValidateArchitecture_RequiresExactLaterUserApproval()
    {
        var history = new[]
        {
            Message(MessageRole.Assistant, 1, "```mermaid\nflowchart LR\nA-->B\n```"),
            Message(MessageRole.User, 2, SoftwareFactoryApprovals.ArchitecturePhrase)
        };

        Assert.Null(SoftwareFactoryApprovals.ValidateArchitecture(history));
        Assert.NotNull(SoftwareFactoryApprovals.ValidateArchitecture(history[..1]));
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

        Assert.Equal("1.1.0", skill.Version);
        Assert.Contains("Existing project iteration", instructions, StringComparison.Ordinal);
        Assert.Contains("pinned into later LLM", instructions, StringComparison.Ordinal);
        Assert.Contains("APPROVE ARCHITECTURE", instructions, StringComparison.Ordinal);
        Assert.Contains("APPROVE UI", instructions, StringComparison.Ordinal);
        Assert.Contains("thread-scoped", instructions, StringComparison.Ordinal);
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