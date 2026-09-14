using System.Collections.Concurrent;
using Microsoft.Azure.Cosmos;
using Newtonsoft.Json;

public interface INoteRepository
{
    Task AddAsync(string text, CancellationToken ct);
    Task<IReadOnlyList<string>> ListAsync(CancellationToken ct);
}

public sealed class InMemoryNoteRepository : INoteRepository
{
    private readonly ConcurrentQueue<string> _notes = new();
    public Task AddAsync(string text, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _notes.Enqueue(text);
        return Task.CompletedTask;
    }
    public Task<IReadOnlyList<string>> ListAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<string>>(_notes.ToArray());
    }
}

public sealed class CosmosNoteRepository(CosmosClient cosmos, IConfiguration configuration) : INoteRepository
{
    private Container Container => cosmos.GetContainer(configuration["Cosmos:Database"]!, configuration["Cosmos:Container"]!);
    public async Task AddAsync(string text, CancellationToken ct)
    {
        var note = new NoteDocument { Text = text };
        await Container.CreateItemAsync(note, new PartitionKey(note.PartitionKey), cancellationToken: ct);
    }
    public async Task<IReadOnlyList<string>> ListAsync(CancellationToken ct)
    {
        var query = new QueryDefinition("SELECT * FROM c WHERE c.type = @type").WithParameter("@type", "fixtureNote");
        using var iterator = Container.GetItemQueryIterator<NoteDocument>(query,
            requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey("fixtureNotes") });
        var notes = new List<string>();
        while (iterator.HasMoreResults)
            notes.AddRange((await iterator.ReadNextAsync(ct)).Select(note => note.Text));
        return notes;
    }
}

public sealed class NoteDocument
{
    [JsonProperty("id")] public string Id { get; set; } = Guid.NewGuid().ToString("N");
    [JsonProperty("partitionKey")] public string PartitionKey { get; set; } = "fixtureNotes";
    [JsonProperty("type")] public string Type { get; set; } = "fixtureNote";
    [JsonProperty("text")] public string Text { get; set; } = "";
}