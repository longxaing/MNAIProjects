using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Options;
using MnaiWork.Api.Configuration;

namespace MnaiWork.Api.Sharing;

public interface IShareBlobStore
{
    Task WriteAsync(string path, byte[] bytes, string contentType, CancellationToken ct);
    Task<byte[]?> ReadAsync(string path, int maxBytes, CancellationToken ct);
    Task DeleteAsync(string shareId, CancellationToken ct);
}

public sealed class ShareBlobStore(BlobServiceClient blobs, IOptions<StorageOptions> options) : IShareBlobStore
{
    private BlobContainerClient Container => blobs.GetBlobContainerClient(options.Value.Container);

    public async Task WriteAsync(string path, byte[] bytes, string contentType, CancellationToken ct)
    {
        await Container.CreateIfNotExistsAsync(PublicAccessType.None, cancellationToken: ct);
        var properties = await Container.GetPropertiesAsync(cancellationToken: ct);
        if (properties.Value.PublicAccess != PublicAccessType.None)
            throw new InvalidOperationException("Sharing requires a private artifact container.");
        await Container.GetBlobClient(path).UploadAsync(new BinaryData(bytes), new BlobUploadOptions
        {
            Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All },
            HttpHeaders = new BlobHttpHeaders { ContentType = contentType, CacheControl = "no-store" }
        }, ct);
    }

    public async Task<byte[]?> ReadAsync(string path, int maxBytes, CancellationToken ct)
    {
        try
        {
            var blob = Container.GetBlobClient(path);
            var response = await blob.DownloadStreamingAsync(cancellationToken: ct);
            using var content = response.Value.Content;
            if (response.Value.Details.ContentLength > maxBytes) throw new InvalidDataException("Snapshot exceeds size limit.");
            using var output = new MemoryStream();
            var buffer = new byte[8192];
            int count;
            while ((count = await content.ReadAsync(buffer, ct)) > 0)
            {
                if (output.Length + count > maxBytes) throw new InvalidDataException("Snapshot exceeds size limit.");
                await output.WriteAsync(buffer.AsMemory(0, count), ct);
            }
            return output.ToArray();
        }
        catch (RequestFailedException error) when (error.Status == 404) { return null; }
    }

    public async Task DeleteAsync(string shareId, CancellationToken ct)
    {
        await foreach (var blob in Container.GetBlobsAsync(BlobTraits.None, BlobStates.None, prefix: $"conversation-shares/{shareId}/", cancellationToken: ct))
            await Container.DeleteBlobIfExistsAsync(blob.Name, cancellationToken: ct);
    }
}