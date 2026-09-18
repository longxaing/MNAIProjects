namespace MnaiWork.Api.Sharing;

public sealed class ShareCleanupService(IServiceScopeFactory scopes, ILogger<ShareCleanupService> logger, TimeProvider clock) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(10));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception error) { logger.LogWarning(error, "Share cleanup will retry on the next interval."); }
        }
    }

    public async Task RunOnceAsync(CancellationToken stoppingToken)
    {
        using var scope = scopes.CreateScope();
        var records = scope.ServiceProvider.GetRequiredService<IShareRepository>();
        var blobs = scope.ServiceProvider.GetRequiredService<IShareBlobStore>();
        foreach (var candidate in await records.ExpiredAsync(clock.GetUtcNow(), stoppingToken))
        {
            try
            {
                var current = await records.GetAsync(candidate.Id, stoppingToken);
                if (current is null) continue;
                var now = clock.GetUtcNow();
                if (current.CleanupAfter > now) continue;
                if (current.State != "revoked" && current.State != "deleting" && current.ExpiresAt > now
                    && (current.State is not ("draft" or "preparing") || current.PreviewExpiresAt > now)) continue;
                current.State = "deleting";
                await records.SaveAsync(current, false, stoppingToken);
                await blobs.DeleteAsync(current.Id, stoppingToken);
                await records.DeleteAsync(current, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
            catch (Exception error) { logger.LogWarning(error, "Share cleanup failed for {ShareId}; continuing with other records.", candidate.Id); }
        }
    }
}