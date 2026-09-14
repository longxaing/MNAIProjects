using System.Net;
using Microsoft.Azure.Cosmos;
using MnaiWork.Api.Data;

namespace MnaiWork.Api.Sharing;

public interface IShareRepository
{
    Task<ShareRecord?> GetAsync(string id, CancellationToken ct);
    Task SaveAsync(ShareRecord record, bool create, CancellationToken ct);
    Task<IReadOnlyList<ShareRecord>> ListAsync(string ownerId, string threadId, CancellationToken ct);
    Task<IReadOnlyList<ShareRecord>> ExpiredAsync(DateTimeOffset now, CancellationToken ct);
    Task DeleteAsync(ShareRecord record, CancellationToken ct);
}

public sealed class ShareRepository(CosmosContext context) : IShareRepository
{
    public async Task<ShareRecord?> GetAsync(string id, CancellationToken ct)
    {
        try
        {
            var response = await context.Shares.ReadItemAsync<ShareRecord>(id, new PartitionKey(id), cancellationToken: ct);
            response.Resource.ETag = response.ETag;
            return response.Resource;
        }
        catch (CosmosException error) when (error.StatusCode == HttpStatusCode.NotFound) { return null; }
    }

    public async Task SaveAsync(ShareRecord record, bool create, CancellationToken ct)
    {
        if (create)
        {
            try
            {
                var response = await context.Shares.CreateItemAsync(record, new PartitionKey(record.Id), cancellationToken: ct);
                record.ETag = response.ETag;
            }
            catch (CosmosException error) when (error.StatusCode == HttpStatusCode.Conflict)
            { throw new InvalidOperationException("Share changed. Refresh before trying again."); }
        }
        else
        {
            if (string.IsNullOrEmpty(record.ETag)) throw new InvalidOperationException("Share version is missing.");
            try
            {
                var response = await context.Shares.ReplaceItemAsync(record, record.Id, new PartitionKey(record.Id),
                    new ItemRequestOptions { IfMatchEtag = record.ETag }, ct);
                record.ETag = response.ETag;
            }
            catch (CosmosException error) when (error.StatusCode == HttpStatusCode.PreconditionFailed)
            { throw new InvalidOperationException("Share changed. Refresh before trying again."); }
        }
    }

    public Task<IReadOnlyList<ShareRecord>> ListAsync(string ownerId, string threadId, CancellationToken ct) => QueryAsync(
        new QueryDefinition("SELECT * FROM c WHERE c.ownerId = @owner AND c.threadId = @thread AND c.state != 'snapshot'")
            .WithParameter("@owner", ownerId).WithParameter("@thread", threadId), ct);

    public Task<IReadOnlyList<ShareRecord>> ExpiredAsync(DateTimeOffset now, CancellationToken ct) => QueryAsync(
        new QueryDefinition("SELECT TOP 50 * FROM c WHERE (NOT IS_DEFINED(c.cleanupAfter) OR c.cleanupAfter <= @now) AND (c.state IN ('revoked', 'deleting') OR c.expiresAt <= @now OR (c.state IN ('draft', 'preparing') AND c.previewExpiresAt <= @now))")
            .WithParameter("@now", now), ct);

    private async Task<IReadOnlyList<ShareRecord>> QueryAsync(QueryDefinition query, CancellationToken ct)
    {
        var results = new List<ShareRecord>();
        using var iterator = context.Shares.GetItemQueryIterator<ShareRecord>(query);
        while (iterator.HasMoreResults) results.AddRange(await iterator.ReadNextAsync(ct));
        return results;
    }

    public async Task DeleteAsync(ShareRecord record, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(record.ETag)) throw new InvalidOperationException("Share version is missing.");
        try
        {
            await context.Shares.DeleteItemAsync<ShareRecord>(record.Id, new PartitionKey(record.Id),
                new ItemRequestOptions { IfMatchEtag = record.ETag }, ct);
        }
        catch (CosmosException error) when (error.StatusCode == HttpStatusCode.NotFound) { }
        catch (CosmosException error) when (error.StatusCode == HttpStatusCode.PreconditionFailed)
        { throw new InvalidOperationException("Share changed during cleanup. Retry with its current version."); }
    }
}