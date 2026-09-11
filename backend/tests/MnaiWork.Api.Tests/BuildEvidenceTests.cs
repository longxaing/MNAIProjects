using System.Text.Json;
using MnaiWork.Api.Agent;
using MnaiWork.Api.Models;
using Xunit;

namespace MnaiWork.Api.Tests;

public sealed class BuildEvidenceTests
{
    [Fact]
    public void FailureCannotBeOverriddenByAssistantSuccessClaim()
    {
        var history = History();
        history[1].ToolSucceeded = false;
        history[1].Content = "CS0246 IConfiguration";
        history.Add(new ChatMessage { Role = MessageRole.Assistant, Sequence = 3, Content = "Build passed and screenshots generated" });
        Assert.Contains("CS0246", BuildEvidence.GetBlockingReply(history));
    }

    [Fact]
    public void CompleteSuccessfulBuildOfCurrentSourceIsAccepted()
        => Assert.Null(BuildEvidence.GetBlockingReply(History()));

    [Theory]
    [InlineData(ArtifactKind.BackendPackage)]
    [InlineData(ArtifactKind.FrontendPackage)]
    [InlineData(ArtifactKind.UiScreenshot)]
    public void MissingArtifactsBlockSuccess(ArtifactKind kind)
    {
        var history = History();
        history[1].Artifacts.RemoveAll(artifact => artifact.Kind == kind);
        Assert.NotNull(BuildEvidence.GetBlockingReply(history));
    }

    [Fact]
    public void NewSourceInvalidatesPreviousSuccess()
    {
        var history = History();
        history.Add(new ChatMessage { Role = MessageRole.Tool, Sequence = 3, Artifacts = new()
        {
            new Artifact { Id = "source-2", Kind = ArtifactKind.SourceZip, FileName = "textblog-source.zip" }
        } });
        Assert.NotNull(BuildEvidence.GetBlockingReply(history));
    }

    [Fact]
    public void LegacySuccessWithoutSourceBindingIsUnverified()
    {
        var history = History();
        history[1].ToolArguments = null;
        Assert.NotNull(BuildEvidence.GetBlockingReply(history));
    }

    [Fact]
    public void SuccessForDifferentSourceIsUnverified()
    {
        var history = History();
        history[1].ToolArguments = JsonSerializer.SerializeToElement(new { projectSlug = "textblog", sourceArchiveFileId = "other" });
        Assert.NotNull(BuildEvidence.GetBlockingReply(history));
    }

    [Fact]
    public void UnrelatedConversationHasNoBuildGate()
        => Assert.Null(BuildEvidence.GetBlockingReply(new[] { new ChatMessage { Role = MessageRole.User, Content = "Hello" } }));

    private static List<ChatMessage> History() => new()
    {
        new ChatMessage { Role = MessageRole.Tool, Sequence = 1, Artifacts = new()
        {
            new Artifact { Id = "source-1", Kind = ArtifactKind.SourceZip, FileName = "textblog-source.zip" }
        } },
        new ChatMessage
        {
            Role = MessageRole.Tool, Sequence = 2, ToolName = "build_test_project", ToolSucceeded = true,
            ToolArguments = JsonSerializer.SerializeToElement(new { projectSlug = "textblog", sourceArchiveFileId = "source-1" }),
            Artifacts = new()
            {
                new Artifact { Id = "report", Kind = ArtifactKind.BuildReport, FileName = "textblog-build-report.txt" },
                new Artifact { Id = "backend", Kind = ArtifactKind.BackendPackage, FileName = "textblog-backend.zip" },
                new Artifact { Id = "frontend", Kind = ArtifactKind.FrontendPackage, FileName = "textblog-frontend.zip" },
                new Artifact { Id = "desktop", Kind = ArtifactKind.UiScreenshot, FileName = "textblog-ui-desktop.png" },
                new Artifact { Id = "mobile", Kind = ArtifactKind.UiScreenshot, FileName = "textblog-ui-mobile.png" }
            }
        }
    };
}