using MnaiWork.Api.Controllers;
using MnaiWork.Api.Models;
using Xunit;

namespace MnaiWork.Api.Tests;

public sealed class FilesControllerTests
{
    [Fact]
    public void OwnsArtifact_RequiresExactBlobPathFromThreadMessages()
    {
        var messages = new[]
        {
            new ChatMessage
            {
                Artifacts =
                {
                    new Artifact { BlobPath = "user/thread/abc-screenshot.png" }
                }
            }
        };

        Assert.True(FilesController.OwnsArtifact(
            messages, "user/thread/abc-screenshot.png"));
        Assert.False(FilesController.OwnsArtifact(
            messages, "other/thread/abc-screenshot.png"));
        Assert.False(FilesController.OwnsArtifact(
            messages, "user/thread/ABC-screenshot.png"));
    }
}