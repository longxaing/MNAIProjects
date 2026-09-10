using System.Collections.Concurrent;
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

public sealed class BuildExecutor : IAsyncDisposable
{
    private const int MaxArchiveBytes = 16 * 1024 * 1024;
    private static readonly TimeSpan MinimumReuseWindow = TimeSpan.FromMinutes(1);

    private readonly IConfiguration _configuration;
    private readonly IE2BSandboxClient _sandboxes;
    private readonly ILogger<BuildExecutor> _logger;
    private readonly ConcurrentDictionary<string, CachedSandbox> _cachedSandboxes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _sandboxGates = new(StringComparer.Ordinal);
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
        CancellationToken ct,
        string? sandboxCacheKey = null)
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
            frontendDirectory), ct, sandboxCacheKey);
    }

    public async Task<BuildProjectResponse> ExecuteAsync(
        BuildProjectRequest request,
        CancellationToken ct,
        string? sandboxCacheKey = null)
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
            _configuration.GetValue("BuildExecution:TotalTimeoutMinutes", 45), 1, 60));
        request = request with
        {
            CommandTimeoutMinutes = Math.Clamp(
                _configuration.GetValue("BuildExecution:CommandTimeoutMinutes", 15), 1, 30),
            TotalTimeoutMinutes = (int)timeout.TotalMinutes,
            PlaywrightVersion = _configuration["BuildExecution:PlaywrightVersion"] ?? "1.62.1"
        };
        using var totalTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        totalTimeout.CancelAfter(timeout);

        try
        {
            _logger.LogInformation(
                "Starting E2B pipeline. TotalTimeoutMinutes={TotalTimeoutMinutes}; " +
                "CommandTimeoutMinutes={CommandTimeoutMinutes}; PlaywrightVersion={PlaywrightVersion}.",
                request.TotalTimeoutMinutes,
                request.CommandTimeoutMinutes,
                request.PlaywrightVersion);
            var result = string.IsNullOrWhiteSpace(sandboxCacheKey)
                ? await ExecuteInEphemeralSandboxAsync(request, templateId, timeout, totalTimeout.Token)
                : await ExecuteInCachedSandboxAsync(
                    request, templateId, sandboxCacheKey, timeout, totalTimeout.Token);
            var failedStep = result.Steps.LastOrDefault(step => !step.Succeeded);
            if (result.Succeeded)
            {
                _logger.LogInformation(
                    "E2B pipeline passed all {StepCount} stages. BuildId={BuildId}.",
                    result.Steps.Count,
                    result.BuildId ?? "missing");
            }
            else
            {
                _logger.LogWarning(
                    "E2B transport succeeded but the sandbox pipeline failed. " +
                    "FailedStep={FailedStep}; Summary={Summary}.",
                    failedStep?.Name ?? "unknown",
                    result.Summary);
            }
            return result;
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

    private async Task<BuildProjectResponse> ExecuteInEphemeralSandboxAsync(
        BuildProjectRequest request,
        string templateId,
        TimeSpan timeout,
        CancellationToken ct)
    {
        await using var sandbox = await _sandboxes.CreateAsync(templateId, timeout, ct);
        return await sandbox.BuildAsync(request, ct);
    }

    private async Task<BuildProjectResponse> ExecuteInCachedSandboxAsync(
        BuildProjectRequest request,
        string templateId,
        string cacheKey,
        TimeSpan sandboxLifetime,
        CancellationToken ct)
    {
        await PruneExpiredSandboxesAsync(cacheKey);
        var gate = _sandboxGates.GetOrAdd(cacheKey, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (_cachedSandboxes.TryGetValue(cacheKey, out var cached)
                && (cached.ExpiresAt <= DateTimeOffset.UtcNow
                    || !string.Equals(cached.TemplateId, templateId, StringComparison.Ordinal)))
            {
                _cachedSandboxes.TryRemove(cacheKey, out _);
                await cached.Session.DisposeAsync();
                cached = null;
            }

            if (cached is null)
            {
                cached = new CachedSandbox(
                    templateId,
                    await _sandboxes.CreateAsync(templateId, sandboxLifetime, ct),
                    DateTimeOffset.UtcNow.Add(GetReuseWindow(sandboxLifetime)));
                _cachedSandboxes[cacheKey] = cached;
                _logger.LogInformation("Created project-scoped cached E2B sandbox.");
            }
            else
            {
                _logger.LogInformation("Reusing project-scoped cached E2B sandbox.");
            }

            var prioritizedRequest = request with { PreferredFirstStage = cached.LastFailedStage };
            try
            {
                var result = await cached.Session.BuildAsync(prioritizedRequest, ct);
                cached.LastFailedStage = result.Succeeded
                    ? null
                    : result.Steps.LastOrDefault(step => !step.Succeeded)?.Name;
                return result;
            }
            catch (HttpRequestException ex) when (ex.StatusCode is System.Net.HttpStatusCode.NotFound
                or System.Net.HttpStatusCode.Gone)
            {
                _logger.LogWarning("Cached E2B sandbox expired remotely; recreating it once.");
                _cachedSandboxes.TryRemove(cacheKey, out _);
                await cached.Session.DisposeAsync();
                cached = new CachedSandbox(
                    templateId,
                    await _sandboxes.CreateAsync(templateId, sandboxLifetime, ct),
                    DateTimeOffset.UtcNow.Add(GetReuseWindow(sandboxLifetime)));
                _cachedSandboxes[cacheKey] = cached;
                var result = await cached.Session.BuildAsync(prioritizedRequest, ct);
                cached.LastFailedStage = result.Succeeded
                    ? null
                    : result.Steps.LastOrDefault(step => !step.Succeeded)?.Name;
                return result;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private static TimeSpan GetReuseWindow(TimeSpan sandboxLifetime)
    {
        var reserve = TimeSpan.FromMinutes(Math.Clamp(
            sandboxLifetime.TotalMinutes / 3,
            2,
            10));
        var reuseWindow = sandboxLifetime - reserve;
        return reuseWindow > MinimumReuseWindow ? reuseWindow : MinimumReuseWindow;
    }

    private async Task PruneExpiredSandboxesAsync(string activeCacheKey)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var item in _cachedSandboxes.Where(item =>
                     !string.Equals(item.Key, activeCacheKey, StringComparison.Ordinal)
                     && item.Value.ExpiresAt <= now))
        {
            var gate = _sandboxGates.GetOrAdd(item.Key, _ => new SemaphoreSlim(1, 1));
            if (!gate.Wait(0))
            {
                continue;
            }
            try
            {
                if (_cachedSandboxes.TryGetValue(item.Key, out var current)
                    && ReferenceEquals(current, item.Value)
                    && _cachedSandboxes.TryRemove(item.Key, out _))
                {
                    await current.Session.DisposeAsync();
                }
            }
            finally
            {
                gate.Release();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var cached in _cachedSandboxes.Values)
        {
            await cached.Session.DisposeAsync();
        }
        _cachedSandboxes.Clear();
        foreach (var gate in _sandboxGates.Values)
        {
            gate.Dispose();
        }
        _sandboxGates.Clear();
    }

    private sealed class CachedSandbox
    {
        public CachedSandbox(
            string templateId,
            IE2BSandboxSession session,
            DateTimeOffset expiresAt)
        {
            TemplateId = templateId;
            Session = session;
            ExpiresAt = expiresAt;
        }

        public string TemplateId { get; }
        public IE2BSandboxSession Session { get; }
        public DateTimeOffset ExpiresAt { get; }
        public string? LastFailedStage { get; set; }
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