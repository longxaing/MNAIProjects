using Azure.Security.KeyVault.Secrets;
using Azure.Storage.Blobs;
using Microsoft.Azure.Cosmos;

public interface IDependencyCheck
{
    Task CheckAsync(CancellationToken ct);
}

public sealed class LocalDependencyCheck : IDependencyCheck
{
    public Task CheckAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}

public sealed class AzureDependencyCheck(
    IConfiguration configuration, BlobServiceClient blobs, CosmosClient cosmos, SecretClient secrets)
    : IDependencyCheck
{
    public async Task CheckAsync(CancellationToken ct)
    {
        await blobs.GetBlobContainerClient(configuration["Storage:Container"]!)
            .GetPropertiesAsync(cancellationToken: ct);
        await cosmos.GetContainer(configuration["Cosmos:Database"]!, configuration["Cosmos:Container"]!)
            .ReadContainerAsync(cancellationToken: ct);
        await foreach (var secretPage in secrets.GetPropertiesOfSecretsAsync(ct).AsPages(pageSizeHint: 1))
        {
            break;
        }
    }
}