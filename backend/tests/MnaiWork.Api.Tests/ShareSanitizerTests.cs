using MnaiWork.Api.Sharing;
using Xunit;

namespace MnaiWork.Api.Tests;

public sealed class ShareSanitizerTests
{
    [Theory]
    [InlineData("[download](https://store.test/x.zip?sig=private)", "private")]
    [InlineData("<img src='https://tracker.test/pixel'>", "tracker.test")]
    [InlineData("contact someone@example.com", "someone@example.com")]
    [InlineData("{\"apiKey\":\"private-value\"}", "private-value")]
    [InlineData("AccountKey=private-value;", "private-value")]
    [InlineData("Bearer private-value", "private-value")]
    [InlineData("/subscriptions/86819ba6-587e-44f1-86c1-027842da66e9/resourceGroups/demo", "86819ba6")]
    [InlineData("[download](/api/threads/id/artifacts/key/download)", "/api/threads")]
    public void RemovesSensitiveContentAndLinks(string input, string forbidden)
        => Assert.DoesNotContain(forbidden, ShareSanitizer.Clean(input));

    [Fact]
    public void PreservesNormalTextAndMermaid()
    {
        const string text = "Architecture\n```mermaid\nflowchart LR\nFE[\"Frontend\"] --> API[\"Backend\"]\n```";
        Assert.Equal(text, ShareSanitizer.Clean(text));
    }
}