using System.Text.Json;
using MnaiWork.Api.Data;
using MnaiWork.Api.Models;
using MnaiWork.Api.Storage;

namespace MnaiWork.Api.Sharing;

public sealed class ShareService(IShareRepository records, IShareBlobStore blobs, IThreadRepository threads,
    IMessageRepository messages, IFileStorage files, TimeProvider clock)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const int MaxImage = 2 * 1024 * 1024;

    public async Task<SharePreview> PreviewAsync(string owner, string threadId, PreviewShareRequest request, CancellationToken ct)
    {
        if (!ValidThreadId(threadId)) throw new KeyNotFoundException();
        var thread = await threads.GetAsync(owner, threadId, ct) ?? throw new KeyNotFoundException();
        if (request.Days is < 1 or > 30 || (request.ScreenshotIds?.Length ?? 0) > 6)
            throw new InvalidDataException("Choose 1-30 days and at most 6 UI screenshots.");
        var existing = await records.ListAsync(owner, threadId, ct);
        var now = clock.GetUtcNow();
        if (existing.Count(record => record.State is "draft" or "preparing" && record.PreviewExpiresAt > now) >= 50)
            throw new InvalidOperationException("Too many pending previews. Wait for an unused preview to expire (up to 30 minutes).");
        var history = await messages.ListAsync(threadId, ct);
        var selected = (request.ScreenshotIds ?? Array.Empty<string>()).ToHashSet(StringComparer.Ordinal);
        var available = history.Where(message => !message.Streaming).SelectMany(message => message.Artifacts)
            .Where(artifact => artifact.Kind == ArtifactKind.UiScreenshot).DistinctBy(artifact => artifact.Id).ToDictionary(artifact => artifact.Id);
        if (selected.Any(id => !available.ContainsKey(id))) throw new InvalidDataException("Select only UI screenshots from this conversation.");
        var record = new ShareRecord { OwnerId = owner, ThreadId = threadId, CreatedAt = now, State = "preparing",
            ExpiresAt = now.AddDays(request.Days), PreviewExpiresAt = now.AddMinutes(30) };
        record.SnapshotPath = $"conversation-shares/{record.Id}/snapshot.json";
        var current = await records.GetAsync(PublicId(threadId), ct);
        record.PreviewVersion = current?.ETag ?? "absent";
        var publicMessages = new List<SharedMessage>();
        var totalChars = 0;
        await records.SaveAsync(record, true, ct);
        try
        {
            foreach (var message in history.OrderBy(message => message.Sequence))
            {
                if (message.Streaming) continue;
                var content = message.Role is MessageRole.User or MessageRole.Assistant ? ShareSanitizer.Clean(message.Content) : "";
                var images = new List<SharedImage>();
                foreach (var artifact in message.Artifacts.Where(artifact => selected.Contains(artifact.Id)))
                {
                    if (!selected.Remove(artifact.Id)) continue;
                    if (artifact.SizeBytes > MaxImage) throw new InvalidDataException("Each screenshot must be at most 2 MB.");
                    var read = await files.OpenReadAsync(artifact.BlobPath, ct) ?? throw new InvalidDataException("Screenshot is unavailable.");
                    using var stream = read.Stream;
                    using var output = new MemoryStream();
                    var buffer = new byte[8192];
                    int count;
                    while ((count = await stream.ReadAsync(buffer, ct)) > 0)
                    {
                        if (output.Length + count > MaxImage) throw new InvalidDataException("Each screenshot must be at most 2 MB.");
                        await output.WriteAsync(buffer.AsMemory(0, count), ct);
                    }
                    var bytes = output.ToArray();
                    if (bytes.Length < 24 || !bytes.AsSpan(0, 8).SequenceEqual(new byte[] {137,80,78,71,13,10,26,10}))
                        throw new InvalidDataException("Only PNG UI screenshots can be shared.");
                    var id = Guid.NewGuid().ToString("N");
                    var path = $"conversation-shares/{record.Id}/{id}.png";
                    await blobs.WriteAsync(path, bytes, "image/png", ct);
                    record.Images[id] = path;
                    images.Add(new SharedImage(id, $"UI screenshot {record.Images.Count}"));
                }
                if (content.Length == 0 && images.Count == 0) continue;
                totalChars += content.Length;
                if (publicMessages.Count >= 150 || totalChars > 300_000) throw new InvalidDataException("Conversation exceeds sharing limit (150 messages / 300,000 characters).");
                publicMessages.Add(new SharedMessage(message.Role == MessageRole.User ? "user" : "assistant", content, images));
            }
            if (publicMessages.Count == 0) throw new InvalidDataException("No completed messages to share.");
            var snapshot = new SharedSnapshot(ShareSanitizer.Clean(thread.Title), now, record.ExpiresAt, publicMessages);
            await blobs.WriteAsync(record.SnapshotPath, JsonSerializer.SerializeToUtf8Bytes(snapshot, Json), "application/json", ct);
            var saved = await records.GetAsync(record.Id, ct) ?? throw new KeyNotFoundException();
            if (saved.State != "preparing") throw new InvalidOperationException("Preview was canceled. Create a new preview.");
            record.ETag = saved.ETag;
            record.State = "draft";
            await records.SaveAsync(record, false, ct);
            return new SharePreview(record.Id, snapshot);
        }
        catch
        {
            await blobs.DeleteAsync(record.Id, CancellationToken.None);
            await records.DeleteAsync(record, CancellationToken.None);
            throw;
        }
    }

    private async Task<ShareRecord> OwnedAsync(string owner, string threadId, string id, CancellationToken ct)
    {
        if (await threads.GetAsync(owner, threadId, ct) is null) throw new KeyNotFoundException();
        var record = await records.GetAsync(id, ct);
        return record is not null && record.OwnerId == owner && record.ThreadId == threadId
            ? record : throw new KeyNotFoundException();
    }

    public async Task PublishAsync(string owner, string threadId, string previewId, CancellationToken ct)
    {
        var record = await OwnedAsync(owner, threadId, previewId, ct);
        if (record.State != "draft" || record.PreviewExpiresAt <= clock.GetUtcNow())
            throw new InvalidOperationException("Preview expired or was already published. Create a new preview.");
        if (await blobs.ReadAsync(record.SnapshotPath, 2 * 1024 * 1024, ct) is null) throw new InvalidOperationException("Preview is incomplete.");
        var current = await records.GetAsync(PublicId(threadId), ct);
        if ((current?.ETag ?? "absent") != record.PreviewVersion)
            throw new InvalidOperationException("Sharing changed since preview. Create a new preview before publishing.");
        if (current?.State == "deleting") throw new InvalidOperationException("Expired share cleanup is in progress. Try again shortly.");
        record.State = "snapshot";
        await records.SaveAsync(record, false, ct);
        var published = new ShareRecord
        {
            Id = PublicId(threadId), OwnerId = owner, ThreadId = threadId, SnapshotPath = record.SnapshotPath,
            Images = new(record.Images), State = "active", CreatedAt = record.CreatedAt, ExpiresAt = record.ExpiresAt,
            ETag = current?.ETag
        };
        await records.SaveAsync(published, current is null, ct);
    }

    public async Task<IReadOnlyList<ShareListItem>> ListAsync(string owner, string threadId, CancellationToken ct)
    {
        if (await threads.GetAsync(owner, threadId, ct) is null) throw new KeyNotFoundException();
        return (await records.ListAsync(owner, threadId, ct)).Where(record => record.Id == PublicId(threadId))
            .Select(record => new ShareListItem(record.Id,
                record.ExpiresAt <= clock.GetUtcNow() || record.State == "draft" && record.PreviewExpiresAt <= clock.GetUtcNow() ? "expired" : record.State,
                record.CreatedAt, record.ExpiresAt)).ToArray();
    }

    public async Task RevokeAsync(string owner, string threadId, string id, CancellationToken ct)
    {
        var record = await OwnedAsync(owner, threadId, id, ct);
        if (record.State == "deleting") throw new InvalidOperationException("Share cleanup is in progress.");
        record.State = "revoked";
        record.CleanupAfter = clock.GetUtcNow().AddMinutes(30);
        await records.SaveAsync(record, false, ct);
    }

    public async Task RevokeThreadAsync(string owner, string threadId, CancellationToken ct)
    {
        foreach (var record in await records.ListAsync(owner, threadId, ct))
            await RevokeAsync(owner, threadId, record.Id, ct);
    }

    private async Task<ShareRecord> PublicAsync(string threadId, CancellationToken ct)
    {
        if (!ValidThreadId(threadId)) throw new KeyNotFoundException();
        var record = await records.GetAsync(PublicId(threadId), ct);
        if (record is null || record.State != "active" || record.ExpiresAt <= clock.GetUtcNow()
            || record.ThreadId != threadId
            || await threads.GetAsync(record.OwnerId, record.ThreadId, ct) is null) throw new KeyNotFoundException();
        return record;
    }

    public async Task<SharedSnapshot> ReadAsync(string threadId, CancellationToken ct)
    {
        var record = await PublicAsync(threadId, ct);
        var bytes = await blobs.ReadAsync(record.SnapshotPath, 2 * 1024 * 1024, ct) ?? throw new KeyNotFoundException();
        return JsonSerializer.Deserialize<SharedSnapshot>(bytes, Json) ?? throw new KeyNotFoundException();
    }

    public async Task<byte[]> ImageAsync(string threadId, string imageId, CancellationToken ct)
    {
        var record = await PublicAsync(threadId, ct);
        return await ImageBytesAsync(record, imageId, ct);
    }

    public async Task<byte[]> PreviewImageAsync(string owner, string threadId, string previewId, string imageId, CancellationToken ct)
    {
        var record = await OwnedAsync(owner, threadId, previewId, ct);
        if (record.State != "draft" || record.PreviewExpiresAt <= clock.GetUtcNow()) throw new KeyNotFoundException();
        return await ImageBytesAsync(record, imageId, ct);
    }

    private async Task<byte[]> ImageBytesAsync(ShareRecord record, string imageId, CancellationToken ct)
    {
        if (!record.Images.TryGetValue(imageId, out var path)) throw new KeyNotFoundException();
        return await blobs.ReadAsync(path, MaxImage, ct) ?? throw new KeyNotFoundException();
    }

    public static bool ValidThreadId(string value) => value.Length is > 0 and <= 128
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    public static string PublicId(string threadId) => "thread-" + threadId;
}