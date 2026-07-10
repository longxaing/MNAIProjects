namespace MnaiWork.Api.Agent;

/// <summary>
/// Long-running background worker. Pulls queued run requests and executes each one in its own DI
/// scope, bounded by a concurrency gate.
/// </summary>
public sealed class AgentRunnerHostedService : BackgroundService
{
    private readonly IAgentRunQueue _queue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AgentRunnerHostedService> _logger;
    private readonly SemaphoreSlim _gate = new(4);

    public AgentRunnerHostedService(
        IAgentRunQueue queue,
        IServiceScopeFactory scopeFactory,
        ILogger<AgentRunnerHostedService> logger)
    {
        _queue = queue;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var request in _queue.DequeueAllAsync(stoppingToken))
        {
            await _gate.WaitAsync(stoppingToken);
            _ = Task.Run(async () =>
            {
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var runner = scope.ServiceProvider.GetRequiredService<AgentRunner>();
                    await runner.RunAsync(request, stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Unhandled error processing run {RunId}.", request.RunId);
                }
                finally
                {
                    _gate.Release();
                }
            }, stoppingToken);
        }
    }
}
