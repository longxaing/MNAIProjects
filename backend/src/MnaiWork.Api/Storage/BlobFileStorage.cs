using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;
using MnaiWork.Api.Configuration;
using MnaiWork.Api.Models;
using Microsoft.Extensions.Options;

namespace MnaiWork.Api.Storage;

public interface IFileStorage
{
    Task<Artifact> UploadAsync(string fileName, ArtifactKind kind, byte[] content, string contentType, CancellationToken ct = default);
    Task<(Stream Stream, string ContentType, string FileName)?> OpenReadAsync(string blobPath, CancellationToken ct = default);
}

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

    public async Task<Artifact> UploadAsync(string fileName, ArtifactKind kind, byte[] content, string contentType, CancellationToken ct = default)
    {
        await EnsureContainerAsync(ct);

        var safeName = SanitizeFileName(fileName);
        var blobName = $"{DateTime.UtcNow:yyyy/MM/dd}/{Guid.NewGuid():N}/{safeName}";
        var blob = _container.GetBlobClient(blobName);

        using var ms = new MemoryStream(content, writable: false);
        await blob.UploadAsync(ms, new BlobUploadOptions
        {
            HttpHeaders = new BlobHttpHeaders { ContentType = contentType }
        }, ct);

        var url = await GenerateDownloadUrlAsync(blob, safeName, ct);

        return new Artifact
        {
            Kind = kind,
            FileName = safeName,
            BlobPath = blobName,
            DownloadUrl = url,
            SizeBytes = content.LongLength
        };
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

    private async Task<string> GenerateDownloadUrlAsync(BlobClient blob, string fileName, CancellationToken ct)
    {
        var expiry = DateTimeOffset.UtcNow.AddMinutes(_options.DownloadLinkTtlMinutes);
        var sas = new BlobSasBuilder
        {
            BlobContainerName = _container.Name,
            BlobName = blob.Name,
            Resource = "b",
            ExpiresOn = expiry,
            ContentDisposition = $"attachment; filename=\"{fileName}\""
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
            return $"/api/files/{Uri.EscapeDataString(blob.Name)}";
        }
    }

    private static string SanitizeFileName(string fileName)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(fileName.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? "document" : cleaned;
    }
}
