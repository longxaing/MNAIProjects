using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;
using MnaiWork.Api.Configuration;
using MnaiWork.Api.Models;
using Microsoft.Extensions.Options;

namespace MnaiWork.Api.Storage;

public interface IFileStorage
{
    Task<Artifact> UploadAsync(ArtifactOwner owner, string fileName, ArtifactKind kind, byte[] content,
        string contentType, CancellationToken ct = default);

    /// <summary>Stores a user-uploaded file under the thread's uploads folder.</summary>
    Task<Attachment> UploadUserFileAsync(ArtifactOwner owner, string fileName, AttachmentKind kind, byte[] content,
        string contentType, CancellationToken ct = default);

    /// <summary>Reads a blob's raw bytes. Returns null if it does not exist.</summary>
    Task<byte[]?> ReadBytesAsync(string blobPath, CancellationToken ct = default);

    Task<(Stream Stream, string ContentType, string FileName)?> OpenReadAsync(string blobPath, CancellationToken ct = default);

    /// <summary>Mints a fresh, time-limited download URL for a stored blob (generated on demand).</summary>
    Task<string> GetDownloadUrlAsync(string blobPath, string fileName, CancellationToken ct = default);

    /// <summary>Deletes every blob under a thread's folder (<c>{userId}/{threadId}/</c>).</summary>
    Task DeleteThreadFilesAsync(ArtifactOwner owner, CancellationToken ct = default);
}

/// <summary>Identifies who a stored artifact belongs to, used to build a meaningful blob path.</summary>
public sealed record ArtifactOwner(string UserId, string ThreadId);

/// <summary>Stores generated documents in Azure Blob Storage and hands out time-limited download links.</summary>
public sealed class BlobFileStorage : IFileStorage
{
    private readonly BlobContainerClient _container;
    private readonly BlobServiceClient _service;
    private readonly StorageOptions _options;
    private readonly ILogger<BlobFileStorage> _logger;
    private int _containerReady;

    public BlobFileStorage(BlobServiceClient service, IOptions<StorageOptions> options, ILogger<BlobFileStorage> logger)
    {
        _service = service;
        _options = options.Value;
        _logger = logger;
        _container = service.GetBlobContainerClient(_options.Container);
    }

    private async Task EnsureContainerAsync(CancellationToken ct)
    {
        if (Interlocked.CompareExchange(ref _containerReady, 1, 0) == 0)
        {
            await _container.CreateIfNotExistsAsync(cancellationToken: ct);
        }
    }

    public async Task<Artifact> UploadAsync(ArtifactOwner owner, string fileName, ArtifactKind kind, byte[] content,
        string contentType, CancellationToken ct = default)
    {
        await EnsureContainerAsync(ct);

        var safeName = SanitizeFileName(fileName);
        // Path is organized by owner: {userId}/{threadId}/{shortId}-{fileName}. The short id keeps
        // names unique within a thread without a full GUID folder per file.
        var shortId = Guid.NewGuid().ToString("N")[..8];
        var blobName = $"{Segment(owner.UserId)}/{Segment(owner.ThreadId)}/{shortId}-{safeName}";
        var blob = _container.GetBlobClient(blobName);

        using var ms = new MemoryStream(content, writable: false);
        await blob.UploadAsync(ms, new BlobUploadOptions
        {
            HttpHeaders = new BlobHttpHeaders { ContentType = contentType }
        }, ct);

        return new Artifact
        {
            Kind = kind,
            FileName = safeName,
            BlobPath = blobName,
            SizeBytes = content.LongLength
        };
    }

    public async Task<Attachment> UploadUserFileAsync(ArtifactOwner owner, string fileName, AttachmentKind kind,
        byte[] content, string contentType, CancellationToken ct = default)
    {
        await EnsureContainerAsync(ct);

        var safeName = SanitizeFileName(fileName);
        var shortId = Guid.NewGuid().ToString("N")[..8];
        // Uploads live in a dedicated subfolder so they never collide with generated artifacts.
        var blobName = $"{Segment(owner.UserId)}/{Segment(owner.ThreadId)}/uploads/{shortId}-{safeName}";
        var blob = _container.GetBlobClient(blobName);

        using var ms = new MemoryStream(content, writable: false);
        await blob.UploadAsync(ms, new BlobUploadOptions
        {
            HttpHeaders = new BlobHttpHeaders { ContentType = contentType }
        }, ct);

        return new Attachment
        {
            Kind = kind,
            FileName = safeName,
            BlobPath = blobName,
            ContentType = contentType,
            SizeBytes = content.LongLength
        };
    }

    public async Task<byte[]?> ReadBytesAsync(string blobPath, CancellationToken ct = default)
    {
        var blob = _container.GetBlobClient(blobPath);
        if (!await blob.ExistsAsync(ct))
        {
            return null;
        }
        using var ms = new MemoryStream();
        await blob.DownloadToAsync(ms, ct);
        return ms.ToArray();
    }

    public async Task<(Stream Stream, string ContentType, string FileName)?> OpenReadAsync(string blobPath, CancellationToken ct = default)
    {
        var blob = _container.GetBlobClient(blobPath);
        if (!await blob.ExistsAsync(ct))
        {
            return null;
        }

        var props = await blob.GetPropertiesAsync(cancellationToken: ct);
        var stream = await blob.OpenReadAsync(cancellationToken: ct);
        var fileName = blobPath.Split('/').Last();
        return (stream, props.Value.ContentType ?? "application/octet-stream", fileName);
    }

    public async Task DeleteThreadFilesAsync(ArtifactOwner owner, CancellationToken ct = default)
    {
        var prefix = $"{Segment(owner.UserId)}/{Segment(owner.ThreadId)}/";
        try
        {
            await foreach (var item in _container.GetBlobsAsync(
                BlobTraits.None, BlobStates.None, prefix, ct).ConfigureAwait(false))
            {
                await _container.DeleteBlobIfExistsAsync(item.Name, cancellationToken: ct);
            }
        }
        catch (Exception ex)
        {
            // Non-fatal: a failed blob cleanup shouldn't block deleting the conversation.
            _logger.LogWarning(ex, "Failed to delete blobs under {Prefix}.", prefix);
        }
    }

    public Task<string> GetDownloadUrlAsync(string blobPath, string fileName, CancellationToken ct = default)
    {
        var blob = _container.GetBlobClient(blobPath);
        return GenerateDownloadUrlAsync(blob, fileName, ct);
    }

    private async Task<string> GenerateDownloadUrlAsync(BlobClient blob, string fileName, CancellationToken ct)
    {
        var expiry = DateTimeOffset.UtcNow.AddMinutes(_options.DownloadLinkTtlMinutes);
        var sas = new BlobSasBuilder
        {
            BlobContainerName = _container.Name,
            BlobName = blob.Name,
            Resource = "b",
            ExpiresOn = expiry,
            ContentDisposition = BuildContentDisposition(fileName)
        };
        sas.SetPermissions(BlobSasPermissions.Read);

        try
        {
            if (blob.CanGenerateSasUri)
            {
                // Shared-key credential (connection string) path.
                return blob.GenerateSasUri(sas).ToString();
            }

            // Entra ID path: mint a user delegation SAS.
            var userDelegationKey = await _service.GetUserDelegationKeyAsync(
                DateTimeOffset.UtcNow.AddMinutes(-5), expiry, ct);
            var uriBuilder = new BlobUriBuilder(blob.Uri)
            {
                Sas = sas.ToSasQueryParameters(userDelegationKey.Value, _service.AccountName)
            };
            return uriBuilder.ToUri().ToString();
        }
        catch (Exception ex)
        {
            // Fall back to a backend-proxied download route if SAS cannot be generated.
            _logger.LogWarning(ex, "Unable to generate SAS URL for {Blob}; falling back to proxy route.", blob.Name);
            var encodedPath = string.Join(
                "/",
                blob.Name.Split('/').Select(Uri.EscapeDataString));
            return $"/api/files/{encodedPath}";
        }
    }

    private static string SanitizeFileName(string fileName)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(fileName.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? "document" : cleaned;
    }

    /// <summary>Sanitizes a value for use as a single blob path segment (no slashes, safe chars).</summary>
    private static string Segment(string value)
    {
        var cleaned = new string((value ?? string.Empty)
            .Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray());
        return string.IsNullOrEmpty(cleaned) ? "unknown" : cleaned;
    }

    /// <summary>
    /// Builds a Content-Disposition value that is safe for the SAS query string. Non-ASCII file
    /// names (e.g. Chinese) must use RFC 5987 <c>filename*=UTF-8''...</c> with percent-encoding,
    /// and are also given an ASCII <c>filename</c> fallback.
    /// </summary>
    private static string BuildContentDisposition(string fileName)
    {
        var isAscii = fileName.All(c => c < 128);
        if (isAscii)
        {
            return $"attachment; filename=\"{fileName}\"";
        }

        var encoded = Uri.EscapeDataString(fileName);
        var asciiFallback = new string(fileName.Select(c => c < 128 ? c : '_').ToArray());
        return $"attachment; filename=\"{asciiFallback}\"; filename*=UTF-8''{encoded}";
    }
}
