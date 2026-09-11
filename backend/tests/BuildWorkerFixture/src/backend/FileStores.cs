using System.Collections.Concurrent;
using Azure.Storage.Blobs;

public interface IAppFileStore
{
    Task WriteAsync(string name, byte[] content, CancellationToken ct);
    Task<byte[]> ReadAsync(string name, CancellationToken ct);
}

public sealed class InMemoryAppFileStore : IAppFileStore
{
    private readonly ConcurrentDictionary<string, byte[]> _files = new(StringComparer.Ordinal);
    public Task WriteAsync(string name, byte[] content, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _files[name] = content.ToArray();
        return Task.CompletedTask;
    }
    public Task<byte[]> ReadAsync(string name, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(_files.TryGetValue(name, out var content)
            ? content.ToArray() : throw new FileNotFoundException("Object not found.", name));
    }
}

public sealed class BlobAppFileStore(BlobServiceClient blobs, IConfiguration configuration) : IAppFileStore
{
    private BlobClient Blob(string name) => blobs.GetBlobContainerClient(configuration["Storage:Container"]!).GetBlobClient(name);
    public async Task WriteAsync(string name, byte[] content, CancellationToken ct)
        => await Blob(name).UploadAsync(new BinaryData(content), overwrite: true, cancellationToken: ct);
    public async Task<byte[]> ReadAsync(string name, CancellationToken ct)
    {
        try
        {
            return (await Blob(name).DownloadContentAsync(ct)).Value.Content.ToArray();
        }
        catch (Azure.RequestFailedException error) when (error.Status == 404)
        {
            throw new FileNotFoundException("Object not found.", name, error);
        }
    }
}