using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace MnaiWork.BuildExecution;

public interface IE2BSandboxClient
{
    Task<IE2BSandboxSession> CreateAsync(
        string templateId,
        TimeSpan timeout,
        CancellationToken ct);
}

public interface IE2BSandboxSession : IAsyncDisposable
{
    Task<BuildProjectResponse> BuildAsync(
        BuildProjectRequest request,
        CancellationToken ct);
}

public sealed class BuildExecutor
{
    private const int MaxArchiveBytes = 16 * 1024 * 1024;

    private readonly IConfiguration _configuration;
    private readonly IE2BSandboxClient _sandboxes;
    private readonly ILogger<BuildExecutor> _logger;
    private readonly object _concurrencyGate = new();
    private TaskCompletionSource _slotChanged = NewSlotSignal();
    private int _activeBuilds;

    public BuildExecutor(
        IConfiguration configuration,
        IE2BSandboxClient sandboxes,
        ILogger<BuildExecutor> logger)
    {
        _configuration = configuration;
        _sandboxes = sandboxes;
        _logger = logger;
    }

    public Task<BuildProjectResponse> ExecuteAsync(
        byte[] sourceArchive,
        string solutionPath,
        string backendProjectPath,
        string frontendDirectory,
        CancellationToken ct)
    {
        if (sourceArchive.Length is 0 or > MaxArchiveBytes)
        {
            throw new InvalidDataException(
                $"Source ZIP must be between 1 byte and {MaxArchiveBytes} bytes.");
        }

        return ExecuteAsync(new BuildProjectRequest(
            Convert.ToBase64String(sourceArchive),
            solutionPath,
            backendProjectPath,
            frontendDirectory), ct);
    }

    public async Task<BuildProjectResponse> ExecuteAsync(
        BuildProjectRequest request,
        CancellationToken ct)
    {
        if (!_configuration.GetValue("BuildExecution:Enabled", false))
        {
            throw new InvalidOperationException(
                "E2B generated project execution is disabled by BuildExecution:Enabled.");
        }

        var templateId = _configuration["E2B:TemplateId"];
        if (string.IsNullOrWhiteSpace(templateId))
        {
            throw new InvalidOperationException(
            "E2B:TemplateId is required. Store it as E2B--TemplateId in Key Vault.");
        }

        using var slot = await AcquireBuildSlotAsync(ct);
        var timeout = TimeSpan.FromMinutes(Math.Clamp(
            _configuration.GetValue("BuildExecution:TotalTimeoutMinutes", 30), 1, 60));
        request = request with
        {
            CommandTimeoutMinutes = Math.Clamp(
                _configuration.GetValue("BuildExecution:CommandTimeoutMinutes", 10), 1, 30),
            TotalTimeoutMinutes = (int)timeout.TotalMinutes,
            PlaywrightVersion = _configuration["BuildExecution:PlaywrightVersion"] ?? "1.62.1"
        };
        using var totalTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        totalTimeout.CancelAfter(timeout);

        try
        {
            await using var sandbox = await _sandboxes.CreateAsync(
                templateId,
                timeout,
                totalTimeout.Token);
            return await sandbox.BuildAsync(request, totalTimeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("E2B build exceeded the configured total timeout.");
            return new BuildProjectResponse(
                false,
                "Build exceeded the configured total timeout.",
                Array.Empty<BuildStepResult>(),
                null,
                null);
        }
    }

    private async Task<IDisposable> AcquireBuildSlotAsync(CancellationToken ct)
    {
        while (true)
        {
            Task wait;
            lock (_concurrencyGate)
            {
                var limit = Math.Clamp(
                    _configuration.GetValue("BuildExecution:MaxConcurrentBuilds", 1), 1, 4);
                if (_activeBuilds < limit)
                {
                    _activeBuilds++;
                    return new BuildSlot(this);
                }
                wait = _slotChanged.Task;
            }
            await wait.WaitAsync(ct);
        }
    }

    private void ReleaseBuildSlot()
    {
        TaskCompletionSource released;
        lock (_concurrencyGate)
        {
            _activeBuilds--;
            released = _slotChanged;
            _slotChanged = NewSlotSignal();
        }
        released.TrySetResult();
    }

    private static TaskCompletionSource NewSlotSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class BuildSlot : IDisposable
    {
        private BuildExecutor? _owner;

        public BuildSlot(BuildExecutor owner) => _owner = owner;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.ReleaseBuildSlot();
    }
}