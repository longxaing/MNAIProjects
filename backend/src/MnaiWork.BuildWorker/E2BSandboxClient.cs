using System.Net;
using System.Net.Http.Json;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace MnaiWork.BuildExecution;

public sealed class E2BSandboxClient : IE2BSandboxClient
{
    private const long MaxBuildResponseBytes = 128L * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly Uri SandboxApiUri = new("https://api.e2b.app/sandboxes");
    private static readonly Regex SandboxIdPattern = new(
        "^[a-z0-9-]+$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private readonly HttpClient _http;
    private readonly IConfiguration _configuration;
    private readonly ILogger<E2BSandboxClient> _logger;

    public E2BSandboxClient(
        HttpClient http,
        IConfiguration configuration,
        ILogger<E2BSandboxClient> logger)
    {
        _http = http;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<IE2BSandboxSession> CreateAsync(
        string templateId,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var apiKey = _configuration["E2B:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException(
                "E2B:ApiKey is required. Store it as E2B--ApiKey in Key Vault.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, SandboxApiUri);
        request.Headers.Add("X-API-Key", apiKey);
        request.Content = JsonContent.Create(new
        {
            templateID = templateId,
            timeout = Math.Max(1, (int)Math.Ceiling(timeout.TotalSeconds)),
            secure = true,
            allow_internet_access = true,
            network = new { allowPublicTraffic = false },
            metadata = new { workload = "mnaiwork-build" }
        }, options: JsonOptions);

        using var response = await _http.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            ct);
        await EnsureSuccessAsync(response, "create E2B sandbox", ct);
        var created = await response.Content.ReadFromJsonAsync<CreateSandboxResponse>(
            JsonOptions,
            ct) ?? throw new HttpRequestException("E2B returned an empty create response.");
        if (!SandboxIdPattern.IsMatch(created.SandboxId)
            || string.IsNullOrWhiteSpace(created.TrafficAccessToken))
        {
            if (!string.IsNullOrWhiteSpace(created.SandboxId))
            {
                await DeleteSandboxAsync(
                    _http,
                    apiKey,
                    created.SandboxId,
                    _logger);
            }
            throw new HttpRequestException(
                "E2B create response contained an invalid sandboxID or omitted trafficAccessToken.");
        }

        _logger.LogInformation(
            "E2B sandbox {SandboxId} created from template {TemplateId} with timeout {TimeoutSeconds}s.",
            created.SandboxId,
            templateId,
            Math.Max(1, (int)Math.Ceiling(timeout.TotalSeconds)));

        return new Session(
            _http,
            apiKey,
            created.SandboxId,
            created.TrafficAccessToken,
            _logger);
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        string operation,
        CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var detail = await response.Content.ReadAsStringAsync(ct);
        if (detail.Length > 2_000)
        {
            detail = detail[..2_000];
        }
        throw new HttpRequestException(
            $"Unable to {operation}: HTTP {(int)response.StatusCode} {detail}",
            null,
            response.StatusCode);
    }

    private static async Task DeleteSandboxAsync(
        HttpClient http,
        string apiKey,
        string sandboxId,
        ILogger logger)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var request = new HttpRequestMessage(
            HttpMethod.Delete,
            new Uri($"https://api.e2b.app/sandboxes/{Uri.EscapeDataString(sandboxId)}"));
        request.Headers.Add("X-API-Key", apiKey);
        try
        {
            using var response = await http.SendAsync(request, timeout.Token);
            if (response.StatusCode != HttpStatusCode.NotFound)
            {
                await EnsureSuccessAsync(response, "delete E2B sandbox", timeout.Token);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            logger.LogWarning(ex, "Unable to delete E2B sandbox {SandboxId}.", sandboxId);
        }
    }

    private sealed class Session : IE2BSandboxSession
    {
        private readonly HttpClient _http;
        private readonly string _apiKey;
        private readonly string _sandboxId;
        private readonly string _trafficAccessToken;
        private readonly ILogger _logger;
        private int _disposed;

        public Session(
            HttpClient http,
            string apiKey,
            string sandboxId,
            string trafficAccessToken,
            ILogger logger)
        {
            _http = http;
            _apiKey = apiKey;
            _sandboxId = sandboxId;
            _trafficAccessToken = trafficAccessToken;
            _logger = logger;
        }

        public async Task<BuildProjectResponse> BuildAsync(
            BuildProjectRequest requestBody,
            CancellationToken ct)
        {
            var runnerUri = new Uri($"https://3000-{_sandboxId}.e2b.app/build");
            var stopwatch = Stopwatch.StartNew();
            using var request = new HttpRequestMessage(HttpMethod.Post, runnerUri);
            request.Headers.Add("e2b-traffic-access-token", _trafficAccessToken);
            request.Content = JsonContent.Create(requestBody, options: JsonOptions);

            using var response = await _http.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                ct);
            await EnsureSuccessAsync(response, "execute E2B build", ct);
            await response.Content.LoadIntoBufferAsync(MaxBuildResponseBytes);
            var result = await response.Content.ReadFromJsonAsync<BuildProjectResponse>(JsonOptions, ct)
                ?? throw new HttpRequestException("E2B runner returned an empty build response.");
            stopwatch.Stop();
            var failedStep = result.Steps.LastOrDefault(step => !step.Succeeded);
            _logger.LogInformation(
                "E2B runner completed for sandbox {SandboxId} in {DurationMilliseconds}ms. " +
                "PipelineSucceeded={PipelineSucceeded}; StepCount={StepCount}; FailedStep={FailedStep}.",
                _sandboxId,
                stopwatch.ElapsedMilliseconds,
                result.Succeeded,
                result.Steps.Count,
                failedStep?.Name ?? "none");
            return result;
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            await DeleteSandboxAsync(_http, _apiKey, _sandboxId, _logger);
            _logger.LogInformation("E2B sandbox {SandboxId} deleted.", _sandboxId);
        }
    }

    private sealed class CreateSandboxResponse
    {
        [JsonPropertyName("sandboxID")]
        public string SandboxId { get; set; } = string.Empty;

        [JsonPropertyName("trafficAccessToken")]
        public string? TrafficAccessToken { get; set; }
    }
}