using System.Net;
using MnaiWork.Api.Models;
using Microsoft.Azure.Cosmos;

namespace MnaiWork.Api.Data;

public interface IThreadRepository
{
    Task<ChatThread> CreateAsync(ChatThread thread, CancellationToken ct = default);
    Task<ChatThread?> GetAsync(string userId, string threadId, CancellationToken ct = default);
    Task<IReadOnlyList<ThreadListItem>> ListAsync(string userId, CancellationToken ct = default);
    Task<ChatThread> UpsertAsync(ChatThread thread, CancellationToken ct = default);
    Task DeleteAsync(string userId, string threadId, CancellationToken ct = default);
}

public interface IMessageRepository
{
    Task<ChatMessage> AddAsync(ChatMessage message, CancellationToken ct = default);
    Task<ChatMessage> UpsertAsync(ChatMessage message, CancellationToken ct = default);
    Task<ChatMessage?> GetAsync(string threadId, string messageId, CancellationToken ct = default);
    Task<IReadOnlyList<ChatMessage>> ListAsync(string threadId, CancellationToken ct = default);
    Task<long> GetNextSequenceAsync(string threadId, CancellationToken ct = default);
}

public interface IRunRepository
{
    Task<AgentRun> CreateAsync(AgentRun run, CancellationToken ct = default);
    Task<AgentRun?> GetAsync(string threadId, string runId, CancellationToken ct = default);
    Task<AgentRun> UpsertAsync(AgentRun run, CancellationToken ct = default);
}

public sealed class ThreadRepository : IThreadRepository
{
    private readonly Container _container;

    public ThreadRepository(CosmosContext context) => _container = context.Threads;

    public async Task<ChatThread> CreateAsync(ChatThread thread, CancellationToken ct = default)
    {
        var response = await _container.CreateItemAsync(thread, new PartitionKey(thread.UserId), cancellationToken: ct);
        return response.Resource;
    }

    public async Task<ChatThread?> GetAsync(string userId, string threadId, CancellationToken ct = default)
    {
        try
        {
            var response = await _container.ReadItemAsync<ChatThread>(threadId, new PartitionKey(userId), cancellationToken: ct);
            return response.Resource;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<ThreadListItem>> ListAsync(string userId, CancellationToken ct = default)
    {
        var query = new QueryDefinition(
            "SELECT c.id, c.title, c.updatedAt FROM c WHERE c.userId = @userId ORDER BY c.updatedAt DESC")
            .WithParameter("@userId", userId);

        var results = new List<ThreadListItem>();
        using var iterator = _container.GetItemQueryIterator<ThreadListItem>(
            query, requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(userId) });
        while (iterator.HasMoreResults)
        {
            foreach (var item in await iterator.ReadNextAsync(ct))
            {
                results.Add(item);
            }
        }

        return results;
    }

    public async Task<ChatThread> UpsertAsync(ChatThread thread, CancellationToken ct = default)
    {
        thread.UpdatedAt = DateTimeOffset.UtcNow;
        var response = await _container.UpsertItemAsync(thread, new PartitionKey(thread.UserId), cancellationToken: ct);
        return response.Resource;
    }

    public Task DeleteAsync(string userId, string threadId, CancellationToken ct = default)
        => _container.DeleteItemAsync<ChatThread>(threadId, new PartitionKey(userId), cancellationToken: ct);
}

public sealed class MessageRepository : IMessageRepository
{
    private readonly Container _container;

    public MessageRepository(CosmosContext context) => _container = context.Messages;

    public async Task<ChatMessage> AddAsync(ChatMessage message, CancellationToken ct = default)
    {
        var response = await _container.CreateItemAsync(message, new PartitionKey(message.ThreadId), cancellationToken: ct);
        return response.Resource;
    }

    public async Task<ChatMessage> UpsertAsync(ChatMessage message, CancellationToken ct = default)
    {
        message.UpdatedAt = DateTimeOffset.UtcNow;
        var response = await _container.UpsertItemAsync(message, new PartitionKey(message.ThreadId), cancellationToken: ct);
        return response.Resource;
    }

    public async Task<ChatMessage?> GetAsync(string threadId, string messageId, CancellationToken ct = default)
    {
        try
        {
            var response = await _container.ReadItemAsync<ChatMessage>(messageId, new PartitionKey(threadId), cancellationToken: ct);
            return response.Resource;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<ChatMessage>> ListAsync(string threadId, CancellationToken ct = default)
    {
        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.threadId = @threadId ORDER BY c.sequence ASC")
            .WithParameter("@threadId", threadId);

        var results = new List<ChatMessage>();
        using var iterator = _container.GetItemQueryIterator<ChatMessage>(
            query, requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(threadId) });
        while (iterator.HasMoreResults)
        {
            foreach (var item in await iterator.ReadNextAsync(ct))
            {
                results.Add(item);
            }
        }

        return results;
    }

    public async Task<long> GetNextSequenceAsync(string threadId, CancellationToken ct = default)
    {
        var query = new QueryDefinition("SELECT VALUE MAX(c.sequence) FROM c WHERE c.threadId = @threadId")
            .WithParameter("@threadId", threadId);

        using var iterator = _container.GetItemQueryIterator<long?>(
            query, requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(threadId) });

        long max = 0;
        while (iterator.HasMoreResults)
        {
            foreach (var value in await iterator.ReadNextAsync(ct))
            {
                if (value.HasValue && value.Value > max)
                {
                    max = value.Value;
                }
            }
        }

        return max + 1;
    }
}

public sealed class RunRepository : IRunRepository
{
    private readonly Container _container;

    public RunRepository(CosmosContext context) => _container = context.Runs;

    public async Task<AgentRun> CreateAsync(AgentRun run, CancellationToken ct = default)
    {
        var response = await _container.CreateItemAsync(run, new PartitionKey(run.ThreadId), cancellationToken: ct);
        return response.Resource;
    }

    public async Task<AgentRun?> GetAsync(string threadId, string runId, CancellationToken ct = default)
    {
        try
        {
            var response = await _container.ReadItemAsync<AgentRun>(runId, new PartitionKey(threadId), cancellationToken: ct);
            return response.Resource;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<AgentRun> UpsertAsync(AgentRun run, CancellationToken ct = default)
    {
        run.UpdatedAt = DateTimeOffset.UtcNow;
        var response = await _container.UpsertItemAsync(run, new PartitionKey(run.ThreadId), cancellationToken: ct);
        return response.Resource;
    }
}
