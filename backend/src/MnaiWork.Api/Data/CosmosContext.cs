using MnaiWork.Api.Configuration;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Options;

namespace MnaiWork.Api.Data;

/// <summary>
/// Owns the <see cref="CosmosClient"/> and exposes the containers used by the app.
/// Also creates the database/containers on first run.
/// </summary>
public sealed class CosmosContext
{
    private readonly CosmosOptions _options;

    public CosmosContext(CosmosClient client, IOptions<CosmosOptions> options)
    {
        Client = client;
        _options = options.Value;
        Threads = client.GetContainer(_options.Database, _options.ThreadsContainer);
        Messages = client.GetContainer(_options.Database, _options.MessagesContainer);
        Runs = client.GetContainer(_options.Database, _options.RunsContainer);
        Users = client.GetContainer(_options.Database, _options.UsersContainer);
    }

    public CosmosClient Client { get; }
    public Container Threads { get; }
    public Container Messages { get; }
    public Container Runs { get; }
    public Container Users { get; }

    /// <summary>Ensures database and containers exist. Safe to call at startup.</summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        Database db = await Client.CreateDatabaseIfNotExistsAsync(_options.Database, cancellationToken: ct);
        await db.CreateContainerIfNotExistsAsync(new ContainerProperties(_options.ThreadsContainer, "/userId"), cancellationToken: ct);
        await db.CreateContainerIfNotExistsAsync(new ContainerProperties(_options.MessagesContainer, "/threadId"), cancellationToken: ct);
        await db.CreateContainerIfNotExistsAsync(new ContainerProperties(_options.RunsContainer, "/threadId"), cancellationToken: ct);
        await db.CreateContainerIfNotExistsAsync(new ContainerProperties(_options.UsersContainer, "/id"), cancellationToken: ct);
    }
}
