using System.Text.Json;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MnaiWork.Api.Controllers;
using MnaiWork.Api.Agent;
using MnaiWork.Api.Infrastructure;
using MnaiWork.Api.Data;
using MnaiWork.Api.Models;
using MnaiWork.Api.Sharing;
using MnaiWork.Api.Storage;
using Xunit;

namespace MnaiWork.Api.Tests;

public sealed class ShareServiceTests
{
    [Fact]
    public async Task DeletingConversationSucceedsWhileSnapshotCleanupIsStuck()
    {
        var fixture = new Fixture();
        var preview = await fixture.Service.PreviewAsync("owner", "thread", new(), default);
        await fixture.Service.PublishAsync("owner", "thread", preview.PreviewId, default);
        var backing = (await fixture.Records.GetAsync(preview.PreviewId, default))!;
        backing.State = "deleting";
        await fixture.Records.SaveAsync(backing, false, default);
        var context = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.NameIdentifier, "owner") }, "test")) };
        var runs = new Runs();
        var controller = new ThreadsController(fixture.Threads, fixture.Messages, runs, fixture.Files,
            new AgentRunQueue(), new CurrentUser(new HttpContextAccessor { HttpContext = context }));
        Assert.IsType<NoContentResult>(await controller.Delete("thread", default));
        Assert.True(runs.Deleted);
        Assert.True(fixture.Files.Deleted);
        Assert.Empty(fixture.Messages.Items);
        Assert.NotNull(await fixture.Records.GetAsync(preview.PreviewId, default));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => fixture.Service.ReadAsync("thread", default));
    }

    [Fact]
    public async Task ConcurrentCleanupCannotDeleteRepublishedPointer()
    {
        var fixture = new Fixture();
        var original = await fixture.Service.PreviewAsync("owner", "thread", new(), default);
        await fixture.Service.PublishAsync("owner", "thread", original.PreviewId, default);
        await fixture.Service.RevokeAsync("owner", "thread", "thread-thread", default);
        fixture.Clock.Now = fixture.Clock.Now.AddMinutes(31);
        using var provider = fixture.CleanupProvider();
        var cleanup = provider.GetRequiredService<ShareCleanupService>();
        var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        fixture.Blobs.BeforeDelete = async id =>
        {
            if (id == "thread-thread" && Interlocked.Increment(ref calls) == 1)
            {
                paused.SetResult();
                await resume.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
        };
        var first = cleanup.RunOnceAsync(default);
        try
        {
            await paused.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await cleanup.RunOnceAsync(default);
            var fresh = await fixture.Service.PreviewAsync("owner", "thread", new(), default);
            await fixture.Service.PublishAsync("owner", "thread", fresh.PreviewId, default);
        }
        finally { resume.TrySetResult(); await first; }
        Assert.NotNull(await fixture.Service.ReadAsync("thread", default));
    }

    [Fact]
    public async Task CleanupFailureDoesNotBlockOtherRecords()
    {
        var fixture = new Fixture();
        var first = await fixture.Service.PreviewAsync("owner", "thread", new(), default);
        var second = await fixture.Service.PreviewAsync("owner", "thread", new(), default);
        fixture.Clock.Now = fixture.Clock.Now.AddMinutes(31);
        fixture.Blobs.BeforeDelete = id => id == first.PreviewId ? throw new IOException("Storage unavailable") : Task.CompletedTask;
        using var provider = fixture.CleanupProvider();
        var cleanup = provider.GetRequiredService<ShareCleanupService>();
        await cleanup.RunOnceAsync(default);
        Assert.Equal("deleting", (await fixture.Records.GetAsync(first.PreviewId, default))!.State);
        Assert.Null(await fixture.Records.GetAsync(second.PreviewId, default));
        fixture.Blobs.BeforeDelete = null;
        await cleanup.RunOnceAsync(default);
        Assert.Null(await fixture.Records.GetAsync(first.PreviewId, default));
    }

    [Fact]
    public async Task HistoricalSnapshotsDoNotExhaustPreviewQuota()
    {
        var fixture = new Fixture();
        for (var index = 0; index < 60; index++)
        {
            var preview = await fixture.Service.PreviewAsync("owner", "thread", new(), default);
            await fixture.Service.PublishAsync("owner", "thread", preview.PreviewId, default);
        }
        Assert.Single(await fixture.Service.ListAsync("owner", "thread", default));
        Assert.NotNull(await fixture.Service.PreviewAsync("owner", "thread", new(), default));
    }

    [Fact]
    public async Task PendingPreviewQuotaRecoversWithoutWaitingForPhysicalCleanup()
    {
        var fixture = new Fixture();
        for (var index = 0; index < 50; index++)
            await fixture.Service.PreviewAsync("owner", "thread", new(), default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.PreviewAsync("owner", "thread", new(), default));
        fixture.Clock.Now = fixture.Clock.Now.AddMinutes(30);
        Assert.NotNull(await fixture.Service.PreviewAsync("owner", "thread", new(), default));
    }

    [Fact]
    public async Task RevocationRetainsVersionMarkerUntilExistingPreviewsExpire()
    {
        var fixture = new Fixture();
        var preview = await fixture.Service.PreviewAsync("owner", "thread", new PreviewShareRequest(), default);
        await fixture.Service.PublishAsync("owner", "thread", preview.PreviewId, default);
        await fixture.Service.RevokeAsync("owner", "thread", "thread-thread", default);
        var record = (await fixture.Records.GetAsync("thread-thread", default))!;
        Assert.Equal("revoked", record.State);
        Assert.True(record.CleanupAfter >= preview.Snapshot.CreatedAt.AddMinutes(30));
    }

    [Theory]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public async Task ShareManagementRequiresTrustedAuthentication(bool allowed, bool authenticated, bool expected)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorization(options => ShareAuthorization.Configure(options, allowed));
        using var provider = services.BuildServiceProvider();
        var identity = new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, "owner") }, authenticated ? "test" : null);
        var result = await provider.GetRequiredService<IAuthorizationService>()
            .AuthorizeAsync(new ClaimsPrincipal(identity), null, ShareAuthorization.OwnerPolicy);
        Assert.Equal(expected, result.Succeeded);
    }

    [Fact]
    public async Task Http_AnonymousReadOnlyShareNeverAuthorizesOriginalConversationOrManagement()
    {
        var fixture = new Fixture();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(fixture.Service);
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddScoped<ICurrentUser, CurrentUser>();
        builder.Services.AddControllers().AddApplicationPart(typeof(SharesController).Assembly);
        builder.Services.AddAuthentication("share-test").AddScheme<AuthenticationSchemeOptions, ShareTestAuth>("share-test", _ => { });
        builder.Services.AddAuthorization(options => ShareAuthorization.Configure(options, true));
        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapControllers();
        await app.StartAsync();
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var client = new HttpClient { BaseAddress = new Uri(address) };
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/shared/thread")).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/threads/thread/shares/preview", new PreviewShareRequest())).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/threads/thread/messages")).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/threads/thread/artifacts/package/download")).StatusCode);
            client.DefaultRequestHeaders.Add("X-Test-User", "owner");
            var previewResponse = await client.PostAsJsonAsync("/api/threads/thread/shares/preview", new PreviewShareRequest());
            previewResponse.EnsureSuccessStatusCode();
            var preview = (await previewResponse.Content.ReadFromJsonAsync<SharePreview>())!;
            var published = await client.PostAsJsonAsync("/api/threads/thread/shares", new PublishShareRequest(preview.PreviewId));
            published.EnsureSuccessStatusCode();
            Assert.Equal("#/share/thread", (await published.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("path").GetString());
            client.DefaultRequestHeaders.Remove("X-Test-User");
            var read = await client.GetAsync("/api/shared/thread");
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);
            Assert.True(read.Headers.CacheControl!.NoStore);
            Assert.Equal("no-referrer", read.Headers.GetValues("Referrer-Policy").Single());
            Assert.Contains("noindex", read.Headers.GetValues("X-Robots-Tag").Single());
            Assert.DoesNotContain("blobPath", await read.Content.ReadAsStringAsync());
            Assert.Equal(HttpStatusCode.MethodNotAllowed, (await client.PostAsJsonAsync("/api/shared/thread", new { content = "continue" })).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.DeleteAsync("/api/threads/thread/shares/thread-thread")).StatusCode);
            client.DefaultRequestHeaders.Add("X-Test-User", "stranger");
            Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync("/api/threads/thread/shares/thread-thread")).StatusCode);
            client.DefaultRequestHeaders.Remove("X-Test-User");
            client.DefaultRequestHeaders.Add("X-Test-User", "owner");
            Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync("/api/threads/thread/shares/thread-thread")).StatusCode);
            client.DefaultRequestHeaders.Remove("X-Test-User");
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/shared/thread")).StatusCode);
        }
        finally { await app.StopAsync(); }
    }

    private sealed class ShareTestAuth(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var user = Request.Headers["X-Test-User"].ToString();
            if (string.IsNullOrEmpty(user)) return Task.FromResult(AuthenticateResult.NoResult());
            var principal = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, user) }, Scheme.Name));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
        }
    }

    [Fact]
    public async Task PublishingFreezesPreviewAndExcludesPrivateFields()
    {
        var fixture = new Fixture();
        await Assert.ThrowsAsync<KeyNotFoundException>(() => fixture.Service.ReadAsync("thread", default));
        var preview = await fixture.Service.PreviewAsync("owner", "thread", new(), default);
        fixture.Messages.Items.Add(new ChatMessage { Role = MessageRole.Assistant, Content = "later private message", Sequence = 9 });
        fixture.Threads.Thread.Title = "changed title";
        await fixture.Service.PublishAsync("owner", "thread", preview.PreviewId, default);
        var snapshot = await fixture.Service.ReadAsync("thread", default);
        Assert.Equal(preview.Snapshot, snapshot with { Messages = preview.Snapshot.Messages });
        var json = JsonSerializer.Serialize(snapshot);
        Assert.DoesNotContain("later private", json);
        Assert.DoesNotContain("internal tool output", json);
        Assert.DoesNotContain("system instruction", json);
        Assert.DoesNotContain("owner@example.com", json);
        Assert.DoesNotContain("private/path", json);
        Assert.DoesNotContain("Token", json);
        Assert.Equal(2, snapshot.Messages.Count);
        Assert.Equal(preview.Snapshot.CreatedAt.AddDays(7), snapshot.ExpiresAt);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => fixture.Service.ImageAsync("thread", "original-artifact", default));
    }

    [Fact]
    public async Task ImagesAreOptInCopiedAndBoundToPublishedSnapshot()
    {
        var fixture = new Fixture();
        var preview = await fixture.Service.PreviewAsync("owner", "thread", new(7, new[] { "original-artifact" }), default);
        var image = Assert.Single(preview.Snapshot.Messages.SelectMany(message => message.Images));
        Assert.NotEqual("original-artifact", image.Id);
        await fixture.Service.PublishAsync("owner", "thread", preview.PreviewId, default);
        fixture.Files.Bytes = new byte[] { 1, 2 };
        Assert.Equal(Files.Png, await fixture.Service.ImageAsync("thread", image.Id, default));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => fixture.Service.ImageAsync("other-thread", image.Id, default));
        await fixture.Service.RevokeAsync("owner", "thread", ShareService.PublicId("thread"), default);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => fixture.Service.ImageAsync("thread", image.Id, default));
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("revoked")]
    [InlineData("deleted")]
    public async Task PublicReadsFailAfterAccessEnds(string scenario)
    {
        var fixture = new Fixture();
        var preview = await fixture.Service.PreviewAsync("owner", "thread", new(), default);
        await fixture.Service.PublishAsync("owner", "thread", preview.PreviewId, default);
        if (scenario == "expired") fixture.Clock.Now = fixture.Clock.Now.AddDays(7);
        if (scenario == "revoked") await fixture.Service.RevokeThreadAsync("owner", "thread", default);
        if (scenario == "deleted") fixture.Threads.Exists = false;
        await Assert.ThrowsAsync<KeyNotFoundException>(() => fixture.Service.ReadAsync("thread", default));
    }

    [Fact]
    public async Task OwnerOnlyManagementAndDraftImages()
    {
        var fixture = new Fixture();
        await Assert.ThrowsAsync<KeyNotFoundException>(() => fixture.Service.PreviewAsync("stranger", "thread", new(), default));
        var preview = await fixture.Service.PreviewAsync("owner", "thread", new(7, new[] { "original-artifact" }), default);
        var image = Assert.Single(preview.Snapshot.Messages.SelectMany(message => message.Images));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => fixture.Service.PublishAsync("stranger", "thread", preview.PreviewId, default));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => fixture.Service.RevokeAsync("stranger", "thread", preview.PreviewId, default));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => fixture.Service.PreviewImageAsync("stranger", "thread", preview.PreviewId, image.Id, default));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => fixture.Service.ReadAsync("thread", default));
        Assert.Equal(Files.Png, await fixture.Service.PreviewImageAsync("owner", "thread", preview.PreviewId, image.Id, default));
    }

    [Fact]
    public async Task RevocationInvalidatesStalePreviewButNewPreviewRepublishesSameThreadLink()
    {
        var fixture = new Fixture();
        var first = await fixture.Service.PreviewAsync("owner", "thread", new(), default);
        await fixture.Service.PublishAsync("owner", "thread", first.PreviewId, default);
        var stale = await fixture.Service.PreviewAsync("owner", "thread", new(), default);
        await fixture.Service.RevokeAsync("owner", "thread", ShareService.PublicId("thread"), default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.PublishAsync("owner", "thread", stale.PreviewId, default));
        var fresh = await fixture.Service.PreviewAsync("owner", "thread", new(), default);
        await fixture.Service.PublishAsync("owner", "thread", fresh.PreviewId, default);
        Assert.NotNull(await fixture.Service.ReadAsync("thread", default));
        Assert.Single(await fixture.Service.ListAsync("owner", "thread", default));
    }

    [Fact]
    public async Task ConcurrentPreviewsCannotOverwritePublishedSnapshot()
    {
        var fixture = new Fixture();
        var first = await fixture.Service.PreviewAsync("owner", "thread", new(), default);
        var second = await fixture.Service.PreviewAsync("owner", "thread", new(), default);
        await fixture.Service.PublishAsync("owner", "thread", first.PreviewId, default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.PublishAsync("owner", "thread", second.PreviewId, default));
    }

    [Fact]
    public async Task ExpiredPreviewAndPackageSelectionAreRejected()
    {
        var fixture = new Fixture();
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Service.PreviewAsync("owner", "thread", new(7, new[] { "package" }), default));
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Service.PreviewAsync("owner", "thread", new(31), default));
        var preview = await fixture.Service.PreviewAsync("owner", "thread", new(), default);
        fixture.Clock.Now = fixture.Clock.Now.AddMinutes(30);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.PublishAsync("owner", "thread", preview.PreviewId, default));
    }

    private sealed class Fixture
    {
        public Records Records { get; } = new();
        public Blobs Blobs { get; } = new();
        public Threads Threads { get; } = new();
        public Messages Messages { get; } = new();
        public Files Files { get; } = new();
        public Clock Clock { get; } = new();
        public ShareService Service => new(Records, Blobs, Threads, Messages, Files, Clock);
        public ServiceProvider CleanupProvider()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IShareRepository>(Records);
            services.AddSingleton<IShareBlobStore>(Blobs);
            services.AddSingleton<TimeProvider>(Clock);
            services.AddSingleton<ShareCleanupService>();
            return services.BuildServiceProvider();
        }
    }
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 9, 14, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class Records : IShareRepository
    {
        private readonly Dictionary<string, ShareRecord> _items = new();
        private static ShareRecord Copy(ShareRecord item) => JsonSerializer.Deserialize<ShareRecord>(JsonSerializer.Serialize(item))!;
        public Task<ShareRecord?> GetAsync(string id, CancellationToken ct) => Task.FromResult(_items.TryGetValue(id, out var value) ? Copy(value) : null);
        public Task SaveAsync(ShareRecord record, bool create, CancellationToken ct)
        {
            if (create ? _items.ContainsKey(record.Id) : !_items.TryGetValue(record.Id, out var prior) || prior.ETag != record.ETag)
                throw new InvalidOperationException("Version conflict");
            var stored = Copy(record);
            stored.ETag = Guid.NewGuid().ToString();
            _items[record.Id] = stored;
            record.ETag = stored.ETag;
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<ShareRecord>> ListAsync(string ownerId, string threadId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ShareRecord>>(_items.Values.Where(item => item.OwnerId == ownerId && item.ThreadId == threadId).Select(Copy).ToArray());
        public Task<IReadOnlyList<ShareRecord>> ExpiredAsync(DateTimeOffset now, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ShareRecord>>(_items.Values.Where(item => item.CleanupAfter <= now &&
                (item.State is "revoked" or "deleting" || item.ExpiresAt <= now ||
                 item.State is "draft" or "preparing" && item.PreviewExpiresAt <= now)).Select(Copy).ToArray());
        public Task DeleteAsync(ShareRecord record, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(record.ETag)) throw new InvalidOperationException("Missing version");
            if (_items.TryGetValue(record.Id, out var current) && current.ETag != record.ETag)
                throw new InvalidOperationException("Version conflict");
            _items.Remove(record.Id);
            return Task.CompletedTask;
        }
    }
    private sealed class Blobs : IShareBlobStore
    {
        private readonly Dictionary<string, byte[]> _items = new();
        public Func<string, Task>? BeforeDelete { get; set; }
        public Task WriteAsync(string path, byte[] bytes, string contentType, CancellationToken ct) { _items.Add(path, bytes.ToArray()); return Task.CompletedTask; }
        public Task<byte[]?> ReadAsync(string path, int maxBytes, CancellationToken ct) => Task.FromResult(_items.GetValueOrDefault(path)?.ToArray());
        public async Task DeleteAsync(string shareId, CancellationToken ct)
        {
            if (BeforeDelete is not null) await BeforeDelete(shareId);
            foreach (var key in _items.Keys.Where(key => key.StartsWith($"conversation-shares/{shareId}/", StringComparison.Ordinal)).ToArray()) _items.Remove(key);
        }
    }
    private sealed class Threads : IThreadRepository
    {
        public bool Exists { get; set; } = true;
        public ChatThread Thread { get; } = new() { Id = "thread", UserId = "owner", Title = "Demo" };
        public Task<ChatThread?> GetAsync(string userId, string threadId, CancellationToken ct = default) => Task.FromResult(Exists && userId == "owner" && threadId == "thread" ? Thread : null);
        public Task<ChatThread> CreateAsync(ChatThread thread, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ThreadListItem>> ListAsync(string userId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ChatThread> UpsertAsync(ChatThread thread, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteAsync(string userId, string threadId, CancellationToken ct = default) { Exists = false; return Task.CompletedTask; }
    }
    private sealed class Messages : IMessageRepository
    {
        public List<ChatMessage> Items { get; } = new()
        {
            new() { Role = MessageRole.User, Content = "Create a demo for owner@example.com", Sequence = 1 },
            new() { Role = MessageRole.Assistant, Content = "Done [file](https://blob.test/private?sig=abc)", Sequence = 2 },
            new() { Role = MessageRole.System, Content = "system instruction", Sequence = 3 },
            new() { Role = MessageRole.Tool, Content = "internal tool output", Sequence = 4, Artifacts = new()
            {
                new Artifact { Id = "original-artifact", Kind = ArtifactKind.UiScreenshot, BlobPath = "private/path", SizeBytes = 100 },
                new Artifact { Id = "package", Kind = ArtifactKind.SourceZip, BlobPath = "private/package" }
            } },
            new() { Role = MessageRole.Assistant, Content = "partial response", Streaming = true, Sequence = 5 }
        };
        public Task<IReadOnlyList<ChatMessage>> ListAsync(string threadId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ChatMessage>>(Items.ToArray());
        public Task<ChatMessage> AddAsync(ChatMessage message, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ChatMessage> UpsertAsync(ChatMessage message, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ChatMessage?> GetAsync(string threadId, string messageId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<long> GetNextSequenceAsync(string threadId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteByThreadAsync(string threadId, CancellationToken ct = default) { Items.Clear(); return Task.CompletedTask; }
    }
    private sealed class Runs : IRunRepository
    {
        public bool Deleted { get; private set; }
        public Task<AgentRun> CreateAsync(AgentRun run, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AgentRun?> GetAsync(string threadId, string runId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AgentRun> UpsertAsync(AgentRun run, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteByThreadAsync(string threadId, CancellationToken ct = default) { Deleted = true; return Task.CompletedTask; }
    }
    private sealed class Files : IFileStorage
    {
        public bool Deleted { get; private set; }
        public static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a6WQAAAAASUVORK5CYII=");
        public byte[] Bytes { get; set; } = Png;
        public Task<(Stream Stream, string ContentType, string FileName)?> OpenReadAsync(string blobPath, CancellationToken ct = default) =>
            Task.FromResult<(Stream, string, string)?>((new MemoryStream(Bytes), "image/png", "image.png"));
        public Task<byte[]?> ReadBytesAsync(string blobPath, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Artifact> UploadAsync(ArtifactOwner owner, string fileName, ArtifactKind kind, byte[] content, string contentType, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Attachment> UploadUserFileAsync(ArtifactOwner owner, string fileName, AttachmentKind kind, byte[] content, string contentType, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string> GetDownloadUrlAsync(string blobPath, string fileName, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteThreadFilesAsync(ArtifactOwner owner, CancellationToken ct = default) { Deleted = true; return Task.CompletedTask; }
    }
}