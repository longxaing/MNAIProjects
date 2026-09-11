using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace MnaiWork.BuildExecution;

public sealed record BuildProjectRequest(
    string SourceArchiveBase64,
    string SolutionPath,
    string BackendProjectPath,
    string FrontendDirectory,
    int CommandTimeoutMinutes = 15,
    int TotalTimeoutMinutes = 45,
    string PlaywrightVersion = "1.62.1",
    string? PreferredFirstStage = null);

public sealed record BuildStepResult(
    string Name,
    bool Succeeded,
    int ExitCode,
    long DurationMilliseconds,
    string Output);

public sealed record BuildProjectResponse(
    bool Succeeded,
    string Summary,
    IReadOnlyList<BuildStepResult> Steps,
    string? BackendPackageBase64,
    string? FrontendPackageBase64,
    string? DesktopScreenshotBase64 = null,
    string? MobileScreenshotBase64 = null,
    string? BuildId = null);

public sealed class LocalBuildPipeline
{
    private const int MaxArchiveBytes = 16 * 1024 * 1024;
    private const int MaxFiles = 2_000;
    private const long MaxExpandedBytes = 256L * 1024 * 1024;
    private const int MaxStepOutputChars = 120_000;
    private const long MaxBackendPackageBytes = 48L * 1024 * 1024;
    private const long MaxFrontendPackageBytes = 24L * 1024 * 1024;
    private const int MaxScreenshotBytes = 5 * 1024 * 1024;

    private readonly IConfiguration _configuration;
    private readonly ILogger<LocalBuildPipeline> _logger;
    private readonly object _concurrencyGate = new();
    private TaskCompletionSource _slotChanged = NewSlotSignal();
    private int _activeBuilds;

    public LocalBuildPipeline(
        IConfiguration configuration,
        ILogger<LocalBuildPipeline> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public Task<BuildProjectResponse> ExecuteAsync(
        byte[] sourceArchive,
        string solutionPath,
        string backendProjectPath,
        string frontendDirectory,
        CancellationToken ct) => ExecuteAsync(new BuildProjectRequest(
            Convert.ToBase64String(sourceArchive),
            solutionPath,
            backendProjectPath,
            frontendDirectory), ct);

    public async Task<BuildProjectResponse> ExecuteAsync(
        BuildProjectRequest request,
        CancellationToken ct)
    {
        if (!_configuration.GetValue("BuildExecution:Enabled", false))
        {
            throw new InvalidOperationException(
                "Sandbox local build pipeline is disabled by BuildExecution:Enabled.");
        }

        using var slot = await AcquireBuildSlotAsync(ct);
        return await ExecuteCoreAsync(request, ct);
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
        private LocalBuildPipeline? _owner;

        public BuildSlot(LocalBuildPipeline owner) => _owner = owner;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.ReleaseBuildSlot();
    }

    private async Task<BuildProjectResponse> ExecuteCoreAsync(
        BuildProjectRequest request,
        CancellationToken ct)
    {
        var archive = DecodeSource(request.SourceArchiveBase64);
        var buildId = Guid.NewGuid().ToString("N");
        var root = Path.Combine(Path.GetTempPath(), "mnai-build", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        using var totalTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        totalTimeout.CancelAfter(TimeSpan.FromMinutes(Math.Clamp(
            _configuration.GetValue("BuildExecution:TotalTimeoutMinutes", 45), 1, 60)));

        try
        {
            ExtractArchive(archive, root);
            var solution = ResolveFile(root, request.SolutionPath, ".sln");
            var backendProject = ResolveFile(root, request.BackendProjectPath, ".csproj");
            var frontend = ResolveDirectory(root, request.FrontendDirectory);
            var packageJsonPath = Path.Combine(frontend, "package.json");
            try
            {
                EnsureBackendTestProjects(root, solution);
            }
            catch (InvalidDataException ex)
            {
                return Failed(new[]
                {
                    new BuildStepResult("backend test configuration", false, 1, 0, ex.Message)
                });
            }
            EnsureManagedIdentityContract(backendProject);
            EnsureFrontendScripts(packageJsonPath);
            try
            {
                EnsureFrontendRuntimeConfiguration(frontend, request.FrontendDirectory);
            }
            catch (InvalidDataException ex)
            {
                return Failed(new[]
                {
                    new BuildStepResult("frontend runtime configuration", false, 1, 0, ex.Message)
                });
            }

            string primaryWorkflow;
            try
            {
                primaryWorkflow = PrimaryWorkflowContract.Load(frontend);
            }
            catch (InvalidDataException ex)
            {
                return Failed(new[] { new BuildStepResult("primary workflow configuration", false, 1, 0, ex.Message) });
            }

            var steps = new List<BuildStepResult>();
            var testResults = Path.Combine(root, "TestResults");
            if (Directory.Exists(testResults))
            {
                Directory.Delete(testResults, recursive: true);
            }
            var npmInstalled = false;
            var frontendTestsCompleted = false;
            var frontendBuildCompleted = false;
            if (string.Equals(request.PreferredFirstStage, "frontend tests", StringComparison.Ordinal)
                || string.Equals(request.PreferredFirstStage, "frontend build", StringComparison.Ordinal))
            {
                if (!await RunNpmRequiredAsync(steps, "npm ci",
                        new[] { "ci", "--prefer-offline" }, frontend, totalTimeout.Token))
                {
                    return Failed(steps);
                }
                npmInstalled = true;
                if (string.Equals(request.PreferredFirstStage, "frontend tests", StringComparison.Ordinal))
                {
                    if (!await RunNpmRequiredAsync(steps, "frontend tests",
                            new[] { "run", "test" }, frontend, totalTimeout.Token))
                    {
                        return Failed(steps);
                    }
                    frontendTestsCompleted = true;
                }
                else
                {
                    if (!await RunNpmRequiredAsync(steps, "frontend build",
                            new[] { "run", "build" }, frontend, totalTimeout.Token))
                    {
                        return Failed(steps);
                    }
                    frontendBuildCompleted = true;
                }
            }
            if (!await RunRequiredAsync(steps, "dotnet restore", "dotnet",
                    new[] { "restore", solution }, root, totalTimeout.Token)
                || !await RunRequiredAsync(steps, "dotnet build", "dotnet",
                    new[] { "build", solution, "-c", "Release", "--no-restore",
                        "-p:AzureCosmosDisableNewtonsoftJsonCheck=false" },
                    root, totalTimeout.Token)
                || !await RunRequiredAsync(steps, "dotnet tests", "dotnet",
                    new[]
                    {
                        "test", solution, "-c", "Release", "--no-build",
                        "--logger", "trx", "--results-directory", testResults
                    }, root, totalTimeout.Token))
            {
                return Failed(steps);
            }
            EnsureBackendTestResults(testResults);

            if ((!npmInstalled && !await RunNpmRequiredAsync(steps, "npm ci",
                    new[] { "ci", "--prefer-offline" }, frontend, totalTimeout.Token))
                || (!frontendTestsCompleted && !await RunNpmRequiredAsync(steps, "frontend tests",
                    new[] { "run", "test" }, frontend, totalTimeout.Token))
                || (!frontendBuildCompleted && !await RunNpmRequiredAsync(steps, "frontend build",
                    new[] { "run", "build" }, frontend, totalTimeout.Token)))
            {
                return Failed(steps);
            }

            (byte[] Desktop, byte[] Mobile)? screenshots;
            var backend = StartBackendProcess(backendProject, root);
            using (backend.Process)
            {
                try
                {
                    await WaitForProcessEndpointAsync(
                        backend.Process, "http://127.0.0.1:5000/health", "backend", totalTimeout.Token);
                    if (!await RunNpmRequiredAsync(steps, "Playwright E2E",
                            new[] { "run", "test:e2e" }, frontend, totalTimeout.Token))
                    {
                        AppendBackendOutput(steps, backend.Output);
                        return Failed(steps);
                    }
                    screenshots = await CaptureUiScreenshotsAsync(
                        steps, frontend, root, primaryWorkflow, totalTimeout.Token);
                    if (screenshots is null)
                    {
                        AppendBackendOutput(steps, backend.Output);
                        return Failed(steps);
                    }
                }
                finally
                {
                    TryKill(backend.Process);
                    await WaitForExitIgnoringErrorsAsync(backend.Process);
                }
            }

            var backendOutput = Path.Combine(root, "out", "backend");
            if (!await RunRequiredAsync(steps, "backend publish", "dotnet",
                    new[]
                    {
                        "publish", backendProject, "-c", "Release", "--no-restore",
                        "-p:AzureCosmosDisableNewtonsoftJsonCheck=false",
                        "--self-contained", "false", "-p:UseAppHost=false", "-p:RuntimeIdentifier=",
                        "-o", backendOutput
                    }, root, totalTimeout.Token))
            {
                return Failed(steps);
            }

            var frontendOutput = Path.Combine(frontend, "dist");
            if (!Directory.Exists(backendOutput) || !Directory.EnumerateFiles(backendOutput).Any())
            {
                throw new InvalidDataException("Backend publish produced no files.");
            }
            if (!File.Exists(Path.Combine(frontendOutput, "index.html")))
            {
                throw new InvalidDataException("Frontend build did not produce dist/index.html.");
            }

            var manifest = JsonSerializer.Serialize(new { buildId });
            await File.WriteAllTextAsync(
                Path.Combine(backendOutput, "deployment-manifest.json"), manifest, totalTimeout.Token);
            await File.WriteAllTextAsync(
                Path.Combine(frontendOutput, "build-manifest.json"), manifest, totalTimeout.Token);

            var backendPackage = ZipDirectory(
                backendOutput,
                MaxBackendPackageBytes,
                256L * 1024 * 1024,
                "backend");
            var frontendPackage = ZipDirectory(
                frontendOutput,
                MaxFrontendPackageBytes,
                128L * 1024 * 1024,
                "frontend");
            return new BuildProjectResponse(
                true,
                $"All {steps.Count} build and test stages passed.",
                steps,
                Convert.ToBase64String(backendPackage),
                Convert.ToBase64String(frontendPackage),
                Convert.ToBase64String(screenshots.Value.Desktop),
                Convert.ToBase64String(screenshots.Value.Mobile),
                buildId);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new BuildProjectResponse(
                false,
                "Build exceeded the configured total timeout.",
                Array.Empty<BuildStepResult>(),
                null,
                null);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Unable to remove build workspace {Workspace}.", root);
            }
        }
    }

    private async Task<bool> RunRequiredAsync(
        ICollection<BuildStepResult> steps,
        string name,
        string executable,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        CancellationToken ct)
    {
        var result = await RunProcessAsync(name, executable, arguments, workingDirectory, ct);
        steps.Add(result);
        return result.Succeeded;
    }

    private Task<bool> RunNpmRequiredAsync(
        ICollection<BuildStepResult> steps,
        string name,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
        {
            return RunRequiredAsync(steps, name, "npm", arguments, workingDirectory, ct);
        }

        var node = FindOnPath("node.exe")
            ?? throw new InvalidOperationException("node.exe was not found on PATH.");
        var npmCli = Path.Combine(Path.GetDirectoryName(node)!, "node_modules", "npm", "bin", "npm-cli.js");
        if (!File.Exists(npmCli))
        {
            throw new InvalidOperationException("npm-cli.js was not found beside node.exe.");
        }
        return RunRequiredAsync(
            steps,
            name,
            node,
            new[] { npmCli }.Concat(arguments).ToArray(),
            workingDirectory,
            ct);
    }

    private async Task<(byte[] Desktop, byte[] Mobile)?> CaptureUiScreenshotsAsync(
        ICollection<BuildStepResult> steps,
        string frontendDirectory,
        string root,
        string primaryWorkflow,
        CancellationToken ct)
    {
        var outputDirectory = Path.Combine(root, "out", "ui");
        Directory.CreateDirectory(outputDirectory);
        var scriptPath = Path.Combine(frontendDirectory, ".mnai-capture-ui.mjs");
        var workflowScriptPath = Path.Combine(frontendDirectory, ".mnai-primary-workflow.mjs");
        using (var stream = typeof(LocalBuildPipeline).Assembly.GetManifestResourceStream(
                   "MnaiWork.BuildExecution.PrimaryWorkflow.mjs")
               ?? throw new InvalidOperationException("Primary workflow verifier resource is missing."))
        using (var reader = new StreamReader(stream))
        {
            await File.WriteAllTextAsync(workflowScriptPath, await reader.ReadToEndAsync(ct), ct);
        }
        await File.WriteAllTextAsync(scriptPath, $$"""
            import { chromium, devices } from "@playwright/test";
            import { verifyPrimaryWorkflow } from "./.mnai-primary-workflow.mjs";
            const contract = {{primaryWorkflow}};

                        const launchOptions = process.platform === "win32"
                            ? { headless: true, channel: "msedge" }
                            : { headless: true };
                        const browser = await chromium.launch(launchOptions);
            try {
              const targets = [
                { name: "desktop", options: { viewport: { width: 1440, height: 900 } } },
                { name: "mobile", options: devices["iPhone 13"] },
              ];
              for (const target of targets) {
                const context = await browser.newContext({ ...target.options, serviceWorkers: "block" });
                const page = await context.newPage();
                                const pageErrors = [];
                                page.on("pageerror", error => pageErrors.push(error.message));
                await page.goto("http://127.0.0.1:4173", { waitUntil: "domcontentloaded", timeout: 30000 });
                await page.waitForTimeout(750);
                                const root = page.locator("#root");
                                await root.waitFor({ state: "visible", timeout: 10000 });
                                const text = (await root.innerText()).trim();
                                const visualElements = await root.locator(
                                    "button,input,select,textarea,a,img,svg,canvas,[role]"
                                ).count();
                                if (text.length < 2 && visualElements === 0) {
                                    throw new Error(`${target.name} page rendered no meaningful UI`);
                                }
                await verifyPrimaryWorkflow(page, contract, target.name);
                                if (pageErrors.length > 0) {
                                    throw new Error(`${target.name} page error: ${pageErrors.join(" | ")}`);
                }
                await page.screenshot({
                  path: `{{outputDirectory.Replace("\\", "/")}}/${target.name}.png`,
                  fullPage: true,
                });
                await context.close();
              }
            } finally {
              await browser.close();
            }
            """, ct);

        using var preview = StartPreviewProcess(frontendDirectory);
        try
        {
            await WaitForFrontendAsync(preview, ct);
            if (!await RunRequiredAsync(
                    steps,
                    "primary workflow and UI screenshots",
                    "node",
                    new[] { scriptPath },
                    frontendDirectory,
                    ct))
            {
                return null;
            }

            var desktop = await File.ReadAllBytesAsync(
                Path.Combine(outputDirectory, "desktop.png"), ct);
            var mobile = await File.ReadAllBytesAsync(
                Path.Combine(outputDirectory, "mobile.png"), ct);
            if (desktop.Length is < 1_000 or > MaxScreenshotBytes
                || mobile.Length is < 1_000 or > MaxScreenshotBytes)
            {
                throw new InvalidDataException("UI screenshot output has an invalid size.");
            }
            return (desktop, mobile);
        }
        finally
        {
            TryKill(preview);
            try
            {
                await preview.WaitForExitAsync(CancellationToken.None);
            }
            catch
            {
            }
            File.Delete(scriptPath);
            File.Delete(workflowScriptPath);
        }
    }

    private Process StartPreviewProcess(string workingDirectory)
    {
        var viteCli = Path.Combine(workingDirectory, "node_modules", "vite", "bin", "vite.js");
        if (!File.Exists(viteCli))
        {
            throw new InvalidOperationException("Vite CLI was not installed by npm ci.");
        }
        var startInfo = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows()
                ? FindOnPath("node.exe")
                    ?? throw new InvalidOperationException("node.exe was not found on PATH.")
                : "node",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in new[]
                 {
                     viteCli, "preview", "--host", "127.0.0.1", "--port", "4173"
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }
        SetRestrictedEnvironment(startInfo);
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException("Unable to start frontend preview server.");
        }
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    private (Process Process, StringBuilder Output) StartBackendProcess(
        string backendProject,
        string workingDirectory)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[]
                 {
                     "run", "--project", backendProject, "-c", "Release", "--no-build",
                     "--no-launch-profile", "--urls", "http://127.0.0.1:5000"
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }
        SetRestrictedEnvironment(startInfo);
        startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        startInfo.Environment["Frontend__Origin"] = "http://127.0.0.1:4173";
        startInfo.Environment["Deployment__Fingerprint"] = "local";
        var output = new StringBuilder();
        var process = new Process { StartInfo = startInfo };
        process.OutputDataReceived += (_, eventArgs) => AppendProcessLine(output, eventArgs.Data);
        process.ErrorDataReceived += (_, eventArgs) => AppendProcessLine(output, eventArgs.Data);
        if (!process.Start())
        {
            throw new InvalidOperationException("Unable to start generated backend for E2E tests.");
        }
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return (process, output);
    }

    private static void AppendProcessLine(StringBuilder output, string? line)
    {
        if (line is null)
        {
            return;
        }
        lock (output)
        {
            if (output.Length < MaxStepOutputChars)
            {
                output.AppendLine(line);
            }
        }
    }

    private static void AppendBackendOutput(List<BuildStepResult> steps, StringBuilder output)
    {
        string text;
        lock (output)
        {
            text = output.ToString();
        }
        if (steps.Count == 0 || string.IsNullOrWhiteSpace(text))
        {
            return;
        }
        var step = steps[^1];
        steps[^1] = step with
        {
            Output = step.Output + "\n\nGenerated API output:\n" + text
        };
    }

    private static async Task WaitForFrontendAsync(Process preview, CancellationToken ct)
        => await WaitForProcessEndpointAsync(
            preview, "http://127.0.0.1:4173", "frontend preview", ct);

    private static async Task WaitForProcessEndpointAsync(
        Process process,
        string endpoint,
        string description,
        CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        for (var attempt = 0; attempt < 30; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            if (process.HasExited)
            {
                throw new InvalidOperationException(
                    $"Generated {description} exited with code {process.ExitCode}.");
            }
            try
            {
                using var response = await http.GetAsync(endpoint, ct);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
            }
            await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
        }
        throw new TimeoutException($"Generated {description} did not become ready.");
    }

    private static async Task WaitForExitIgnoringErrorsAsync(Process process)
    {
        try
        {
            await process.WaitForExitAsync(CancellationToken.None);
        }
        catch
        {
        }
    }

    private async Task<BuildStepResult> RunProcessAsync(
        string name,
        string executable,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        SetRestrictedEnvironment(startInfo);

        using var process = new Process { StartInfo = startInfo };
        var output = new StringBuilder();
        var outputLock = new object();
        void Append(string? line)
        {
            if (line is null)
            {
                return;
            }
            lock (outputLock)
            {
                if (output.Length < MaxStepOutputChars)
                {
                    output.AppendLine(line);
                }
            }
        }
        process.OutputDataReceived += (_, eventArgs) => Append(eventArgs.Data);
        process.ErrorDataReceived += (_, eventArgs) => Append(eventArgs.Data);

        var stopwatch = Stopwatch.StartNew();
        if (!process.Start())
        {
            throw new InvalidOperationException($"Unable to start build stage '{name}'.");
        }
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var commandTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        commandTimeout.CancelAfter(TimeSpan.FromMinutes(Math.Clamp(
            _configuration.GetValue("BuildExecution:CommandTimeoutMinutes", 15), 1, 30)));
        try
        {
            await process.WaitForExitAsync(commandTimeout.Token);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }
        stopwatch.Stop();

        if (output.Length >= MaxStepOutputChars)
        {
            output.AppendLine("[output truncated]");
        }
        return new BuildStepResult(
            name,
            process.ExitCode == 0,
            process.ExitCode,
            stopwatch.ElapsedMilliseconds,
            output.ToString());
    }

    private static void SetRestrictedEnvironment(ProcessStartInfo startInfo)
    {
        var allowed = new[]
        {
            "PATH", "SystemRoot", "WINDIR", "ProgramFiles", "ProgramFiles(x86)",
            "HOME", "USERPROFILE", "APPDATA", "LOCALAPPDATA", "TEMP", "TMP",
            "DOTNET_ROOT", "DOTNET_CLI_HOME", "NUGET_PACKAGES", "NODE_PATH",
            "PLAYWRIGHT_BROWSERS_PATH"
        };
        var values = allowed
            .Select(name => (Name: name, Value: Environment.GetEnvironmentVariable(name)))
            .Where(item => !string.IsNullOrWhiteSpace(item.Value))
            .ToList();
        startInfo.Environment.Clear();
        foreach (var value in values)
        {
            startInfo.Environment[value.Name] = value.Value!;
        }
        startInfo.Environment["CI"] = "true";
        startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        startInfo.Environment["DOTNET_NOLOGO"] = "1";
        startInfo.Environment["NUGET_XMLDOC_MODE"] = "skip";
    }

    private static byte[] DecodeSource(string content)
    {
        try
        {
            var bytes = Convert.FromBase64String(content);
            if (bytes.Length == 0 || bytes.Length > MaxArchiveBytes)
            {
                throw new InvalidDataException(
                    $"Source ZIP must be between 1 byte and {MaxArchiveBytes} bytes.");
            }
            return bytes;
        }
        catch (FormatException ex)
        {
            throw new InvalidDataException("SourceArchiveBase64 is invalid.", ex);
        }
    }

    private static void ExtractArchive(byte[] bytes, string root)
    {
        var rootPrefix = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        var fileCount = 0;
        long expandedBytes = 0;
        using var stream = new MemoryStream(bytes, writable: false);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        foreach (var entry in archive.Entries)
        {
            var target = Path.GetFullPath(Path.Combine(root, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
            if (!target.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"ZIP entry '{entry.FullName}' escapes the workspace.");
            }
            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(target);
                continue;
            }

            fileCount++;
            expandedBytes = checked(expandedBytes + entry.Length);
            if (fileCount > MaxFiles || expandedBytes > MaxExpandedBytes)
            {
                throw new InvalidDataException("Source ZIP exceeds extraction limits.");
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: false);
        }
        if (fileCount == 0)
        {
            throw new InvalidDataException("Source ZIP contains no files.");
        }
    }

    private static string ResolveFile(string root, string relativePath, string extension)
    {
        var path = ResolvePath(root, relativePath);
        if (!string.Equals(Path.GetExtension(path), extension, StringComparison.OrdinalIgnoreCase)
            || !File.Exists(path))
        {
            throw new InvalidDataException($"Required {extension} file '{relativePath}' was not found.");
        }
        return path;
    }

    private static string ResolveDirectory(string root, string relativePath)
    {
        var path = ResolvePath(root, relativePath);
        if (!Directory.Exists(path))
        {
            throw new InvalidDataException($"Required directory '{relativePath}' was not found.");
        }
        return path;
    }

    private static string ResolvePath(string root, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
        {
            throw new InvalidDataException("Build paths must be non-empty relative paths.");
        }
        var rootPrefix = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!path.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Build path '{relativePath}' escapes the workspace.");
        }
        return path;
    }

    private void EnsureFrontendScripts(string packageJsonPath)
    {
        if (!File.Exists(packageJsonPath))
        {
            throw new InvalidDataException("Frontend package.json was not found.");
        }
        using var document = JsonDocument.Parse(File.ReadAllBytes(packageJsonPath));
        if (!document.RootElement.TryGetProperty("scripts", out var scripts))
        {
            throw new InvalidDataException("Frontend package.json must define scripts.");
        }
        foreach (var required in new[] { "test", "build", "test:e2e", "preview" })
        {
            if (!scripts.TryGetProperty(required, out var script)
                || string.IsNullOrWhiteSpace(script.GetString()))
            {
                throw new InvalidDataException($"Frontend package.json must define '{required}'.");
            }
        }

        var testScript = scripts.GetProperty("test").GetString()!;
        var e2eScript = scripts.GetProperty("test:e2e").GetString()!;
        if (!testScript.Contains("vitest", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Frontend 'test' must execute Vitest.");
        }
        if (!e2eScript.Contains("playwright test", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Frontend 'test:e2e' must execute Playwright tests.");
        }
        if (!scripts.GetProperty("preview").GetString()!
            .Contains("vite preview", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Frontend 'preview' must execute Vite preview.");
        }
        if (!document.RootElement.TryGetProperty("devDependencies", out var dependencies)
            || !dependencies.TryGetProperty("vitest", out _)
            || !dependencies.TryGetProperty("@playwright/test", out var playwrightVersion))
        {
            throw new InvalidDataException(
                "Frontend devDependencies must include vitest and @playwright/test.");
        }
            var requiredPlaywrightVersion = _configuration["BuildExecution:PlaywrightVersion"] ?? "1.62.1";
            if (!string.Equals(
                playwrightVersion.GetString(), requiredPlaywrightVersion, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                $"Frontend @playwright/test must be exactly {requiredPlaywrightVersion}.");
            }

        var frontendDirectory = Path.GetDirectoryName(packageJsonPath)!;
        var e2eFiles = Directory.Exists(Path.Combine(frontendDirectory, "tests", "e2e"))
            ? Directory.EnumerateFiles(
                Path.Combine(frontendDirectory, "tests", "e2e"), "*.ts", SearchOption.AllDirectories)
                .ToList()
            : new List<string>();
        if (e2eFiles.Count == 0
            || !e2eFiles.Select(File.ReadAllText)
                .Any(source => source.Contains("page.goto", StringComparison.Ordinal)))
        {
            throw new InvalidDataException(
                "Frontend E2E tests must open the rendered application with page.goto.");
        }
    }

    private static void EnsureBackendTestProjects(string root, string solutionPath)
    {
        var solution = File.ReadAllText(solutionPath);
        var requiredDirectories = new[]
        {
            Path.Combine(root, "tests", "backend.unit"),
            Path.Combine(root, "tests", "backend.integration")
        };
        foreach (var directory in requiredDirectories)
        {
            var projects = Directory.Exists(directory)
                ? Directory.EnumerateFiles(directory, "*.csproj", SearchOption.TopDirectoryOnly).ToList()
                : new List<string>();
            if (projects.Count != 1)
            {
                throw new InvalidDataException(
                    $"Required backend test directory '{Path.GetRelativePath(root, directory)}' " +
                    "must contain exactly one project.");
            }
            var relative = Path.GetRelativePath(root, projects[0]);
            var project = XDocument.Load(projects[0]);
            var packages = project.Descendants()
                .Where(element => element.Name.LocalName == "PackageReference")
                .Select(element => element.Attribute("Include")?.Value)
                .OfType<string>()
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var requiredPackage in new[]
                     { "Microsoft.NET.Test.Sdk", "xunit", "xunit.runner.visualstudio" })
            {
                if (!packages.Contains(requiredPackage))
                {
                    throw new InvalidDataException(
                        $"{relative.Replace('\\', '/')}: missing required PackageReference '{requiredPackage}'. " +
                        "Restore the template test package references (Microsoft.NET.Test.Sdk 17.11.1, " +
                        "xunit 2.9.2, xunit.runner.visualstudio 2.8.2), preserving product tests and " +
                        "ProjectReference. The xUnit adapter alone is not the test SDK. A missing Test SDK " +
                        "can cause testhost startup failures mentioning transitive dependencies such as " +
                        "Azure.Core.dll. Do not add arbitrary Azure.Core versions or disable Cosmos checks; " +
                        "repair the test project configuration and rerun build_test_project.");
                }
            }
            if (!solution.Contains(relative, StringComparison.OrdinalIgnoreCase)
                && !solution.Contains(relative.Replace('/', '\\'), StringComparison.OrdinalIgnoreCase)
                && !solution.Contains(relative.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Backend test project '{relative}' must be included in the solution.");
            }
        }
    }

    private static void EnsureBackendTestResults(string resultsDirectory)
    {
        var files = Directory.Exists(resultsDirectory)
            ? Directory.EnumerateFiles(resultsDirectory, "*.trx", SearchOption.AllDirectories).ToList()
            : new List<string>();
        if (files.Count < 2)
        {
            throw new InvalidDataException(
                "Backend unit and integration projects must each produce a TRX result.");
        }
        foreach (var file in files)
        {
            var counters = XDocument.Load(file).Descendants()
                .FirstOrDefault(element => element.Name.LocalName == "Counters");
            if (!int.TryParse(counters?.Attribute("total")?.Value, out var total) || total < 1)
            {
                throw new InvalidDataException(
                    $"Backend test result '{Path.GetFileName(file)}' contains no tests.");
            }
        }
    }

    private static void EnsureManagedIdentityContract(string backendProjectPath)
    {
        var project = XDocument.Load(backendProjectPath);
        var packages = project.Descendants("PackageReference")
            .Select(element => element.Attribute("Include")?.Value)
            .Where(value => value is not null)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var requiredPackages = new[]
        {
            "Azure.Identity",
            "Azure.Security.KeyVault.Secrets",
            "Azure.Storage.Blobs",
            "Microsoft.Azure.Cosmos"
        };
        foreach (var package in requiredPackages)
        {
            if (!packages.Contains(package))
            {
                throw new InvalidDataException(
                    $"Backend project must reference {package} for managed identity access.");
            }
        }

        var backendDirectory = Path.GetDirectoryName(backendProjectPath)!;
        var sources = Directory.EnumerateFiles(
                backendDirectory, "*.cs", SearchOption.AllDirectories)
            .Select(File.ReadAllText)
            .ToList();
        var source = string.Join('\n', sources);
        var createdTypes = sources
            .SelectMany(text => CSharpSyntaxTree.ParseText(text).GetRoot()
                .DescendantNodes()
                .OfType<ObjectCreationExpressionSyntax>())
            .Select(creation => creation.Type.ToString().Split('.').Last())
            .ToHashSet(StringComparer.Ordinal);
        foreach (var symbol in new[]
                 {
                     "DefaultAzureCredential",
                     "BlobServiceClient",
                     "CosmosClient",
                     "SecretClient"
                 })
        {
            if (!createdTypes.Contains(symbol))
            {
                throw new InvalidDataException(
                    $"Backend source must use {symbol} for managed identity resource access.");
            }
        }
        foreach (var token in new[]
                 {
                     "Frontend:Origin",
                     "Deployment:Fingerprint",
                     "GetPropertiesOfSecretsAsync",
                     "ReadContainerAsync",
                     "GetBlobContainerClient",
                     "\"/ready\"",
                     "UseCors"
                 })
        {
            if (!source.Contains(token, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Backend source must preserve deployment contract token {token}.");
            }
        }

        if (source.Contains("Storage:ConnectionString", StringComparison.OrdinalIgnoreCase)
            || source.Contains("Cosmos:Key", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "Generated backend must not use Storage connection strings or Cosmos keys.");
        }
    }

    private static void EnsureFrontendRuntimeConfiguration(string frontendDirectory, string relativeDirectory)
    {
        var prefix = relativeDirectory.Replace('\\', '/').TrimEnd('/');
        var missing = new List<string>();
        var indexPath = Path.Combine(frontendDirectory, "index.html");
        if (!File.Exists(indexPath))
        {
            missing.Add($"{prefix}/index.html: missing file; restore the HTML entry point with a /runtime-config.js script before the app entry script.");
        }
        else if (!File.ReadAllText(indexPath).Contains("/runtime-config.js", StringComparison.Ordinal))
        {
            missing.Add($"{prefix}/index.html: missing /runtime-config.js script reference; load it before the app entry script.");
        }
        var runtimeConfig = Path.Combine(frontendDirectory, "public", "runtime-config.js");
        if (!File.Exists(runtimeConfig))
        {
            missing.Add($"{prefix}/public/runtime-config.js: missing file; restore the template window.__APP_CONFIG__ placeholder with apiBaseUrl for deployment injection.");
        }
        var sourceDirectory = Path.Combine(frontendDirectory, "src");
        var sourceFiles = Directory.Exists(sourceDirectory)
            ? Directory.EnumerateFiles(sourceDirectory, "*.*", SearchOption.AllDirectories)
            : Enumerable.Empty<string>();
        var source = string.Join('\n', sourceFiles
            .Where(path => Path.GetExtension(path) is ".ts" or ".tsx")
            .Select(File.ReadAllText));
        foreach (var requiredToken in new[] { "__APP_CONFIG__", "apiBaseUrl" })
        {
            if (!source.Contains(requiredToken, StringComparison.Ordinal))
            {
                missing.Add($"{prefix}/src: missing {requiredToken} in TypeScript sources; restore the typed window.__APP_CONFIG__.apiBaseUrl reader. A getApiBaseUrl wrapper is allowed.");
            }
        }
        if (missing.Count > 0)
        {
            throw new InvalidDataException(
                string.Join('\n', missing) + "\nRead the listed existing files and API URL helper from the latest SourceZip " +
                "with read_project_workspace (list files first if a path is missing). Repair all implicated files " +
                "in one update_project_workspace revision, then rerun build_test_project. " +
                "This is repairable source configuration, not E2B initialization failure. " +
                "Do not stop after promising a repair or request another architecture approval.");
        }
    }

    private static byte[] ZipDirectory(
        string directory,
        long maxBytes,
        long maxExpandedBytes,
        string packageName)
    {
        var files = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();
        var expandedBytes = files.Sum(file => new FileInfo(file).Length);
        if (expandedBytes > maxExpandedBytes)
        {
            throw new InvalidDataException(
                $"Expanded {packageName} output exceeds {maxExpandedBytes / 1024 / 1024} MB.");
        }
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in files)
            {
                var relative = Path.GetRelativePath(directory, file).Replace('\\', '/');
                var entry = archive.CreateEntry(relative, CompressionLevel.Optimal);
                using (var input = File.OpenRead(file))
                using (var target = entry.Open())
                {
                    input.CopyTo(target);
                }
                if (output.Length > maxBytes)
                {
                    throw new InvalidDataException(
                        $"Compressed {packageName} package exceeds {maxBytes / 1024 / 1024} MB.");
                }
            }
        }
        if (output.Length > maxBytes)
        {
            throw new InvalidDataException(
                $"Compressed {packageName} package exceeds {maxBytes / 1024 / 1024} MB.");
        }
        return output.ToArray();
    }

    private static BuildProjectResponse Failed(IReadOnlyList<BuildStepResult> steps)
    {
        var failed = steps.Last(step => !step.Succeeded);
        return new BuildProjectResponse(
            false,
            $"Stage '{failed.Name}' failed with exit code {failed.ExitCode}.",
            steps,
            null,
            null);
    }

    private static string? FindOnPath(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }
        return path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(directory => Path.Combine(directory.Trim('"'), fileName))
            .FirstOrDefault(File.Exists);
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch
        {
        }
    }
}