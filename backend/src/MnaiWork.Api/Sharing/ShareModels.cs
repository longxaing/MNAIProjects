using System.Text.Json.Serialization;

namespace MnaiWork.Api.Sharing;

public sealed class ShareRecord
{
    [JsonPropertyName("id")] public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string OwnerId { get; set; } = "";
    public string ThreadId { get; set; } = "";
    public string SnapshotPath { get; set; } = "";
    public string PreviewVersion { get; set; } = "";
    public string State { get; set; } = "draft";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset PreviewExpiresAt { get; set; }
    public DateTimeOffset CleanupAfter { get; set; }
    public Dictionary<string, string> Images { get; set; } = new();
    [JsonPropertyName("_etag")] public string? ETag { get; set; }
}

public sealed record SharedImage(string Id, string Name);
public sealed record SharedMessage(string Role, string Content, IReadOnlyList<SharedImage> Images);
public sealed record SharedSnapshot(string Title, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt,
    IReadOnlyList<SharedMessage> Messages);
public sealed record PreviewShareRequest(int Days = 7, string[]? ScreenshotIds = null);
public sealed record PublishShareRequest(string PreviewId);
public sealed record SharePreview(string PreviewId, SharedSnapshot Snapshot);
public sealed record ShareListItem(string Id, string State, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt);