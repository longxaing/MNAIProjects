using System.IO.Compression;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Text;
using System.Text.Json;
using MnaiWork.BuildExecution;
using MnaiWork.Api.Configuration;
using MnaiWork.Api.Data;
using MnaiWork.Api.Infrastructure;
using MnaiWork.Api.Models;
using MnaiWork.Api.Storage;
using Microsoft.Extensions.Options;

namespace MnaiWork.Api.Agent.Tools;

public sealed class CreateProjectWorkspaceTool : IAgentTool
{
    private const string TemplatePrefix = "MnaiWork.Api.Agent.ProjectTemplate/";
    private static readonly Regex SlugPattern = new(
        "^[a-z0-9](?:[a-z0-9-]{1,22}[a-z0-9])?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly IFileStorage _storage;
    private readonly IServiceScopeFactory _scopeFactory;

    internal static bool IsValidSlug(string? slug) => slug is not null && SlugPattern.IsMatch(slug);

    public CreateProjectWorkspaceTool(
        IFileStorage storage,
        IServiceScopeFactory scopeFactory)
    {
        _storage = storage;
        _scopeFactory = scopeFactory;
    }

    public string Name => "create_project_workspace";

    public string Description =>
        "Create a new immutable React + ASP.NET Core project source ZIP from the platform template. " +
        "The template includes a solution, npm lockfile, xUnit unit/integration tests, Vitest, and " +
        "Playwright. Requires exact user approval after a Mermaid architecture proposal.";

    public string ParametersSchema => """
    {
      "type": "object",
      "properties": {
        "projectSlug": {
          "type": "string",
          "description": "3-24 lowercase letters, numbers, or hyphens; start and end alphanumeric."
        }
      },
      "required": ["projectSlug"]
    }
    """;

    public async Task<ToolResult> ExecuteAsync(
        JsonElement arguments,
        ToolContext context,
        CancellationToken ct)
    {
        var slug = arguments.TryGetProperty("projectSlug", out var slugElement)
            ? slugElement.GetString()
            : null;
        if (!IsValidSlug(slug))
        {
            return ToolResult.Fail(
                "projectSlug must be 3-24 lowercase letters, numbers, or hyphens and start/end alphanumeric.");
        }

        using (var scope = _scopeFactory.CreateScope())
        {
            var messages = scope.ServiceProvider.GetRequiredService<IMessageRepository>();
            var history = await messages.ListAsync(context.ThreadId, ct);
            var approvalError = SoftwareFactoryApprovals.ValidateArchitecture(history);
            if (approvalError is not null)
            {
                return ToolResult.Fail(approvalError);
            }
            var existingRevision = SoftwareFactoryApprovals.FindLatestSourceRevision(history, slug!);
            if (existingRevision is not null)
            {
                return ToolResult.Fail(
                    $"Project '{slug}' already exists. Continue from latest sourceArchiveFileId=" +
                    $"{existingRevision.Id} with update_project_workspace.");
            }
        }

        var assembly = Assembly.GetExecutingAssembly();
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var resourceName in assembly.GetManifestResourceNames()
                     .Where(name => name.StartsWith(TemplatePrefix, StringComparison.Ordinal)))
        {
            var path = resourceName[TemplatePrefix.Length..].Replace('\\', '/');
            using var stream = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException($"Embedded project template '{resourceName}' is missing.");
            using var reader = new StreamReader(stream, Encoding.UTF8, true);
            files[ProjectArchive.NormalizePath(path)] = reader.ReadToEnd();
        }
        ProjectArchive.Validate(files);

        var archive = ProjectArchive.Create(files);
        var artifact = await _storage.UploadAsync(
            new ArtifactOwner(context.UserId, context.ThreadId),
            $"{slug}-source.zip",
            ArtifactKind.SourceZip,
            archive,
            "application/zip",
            ct);
        return ToolResult.Ok(
            $"Project workspace created from the platform template. " +
            $"sourceArchiveFileId={artifact.Id}; files={files.Count}; sizeBytes={archive.Length}.",
            artifact);
    }
}

public sealed class ProjectFileChange
{
    public string Path { get; set; } = string.Empty;
    public string? Content { get; set; }
    public bool Delete { get; set; }
}

public sealed class UpdateProjectWorkspaceRequest
{
    public string ProjectSlug { get; set; } = string.Empty;
    public string? SourceArchiveFileId { get; set; }
    public List<ProjectFileChange> Files { get; set; } = new();
}

public sealed class UpdateProjectWorkspaceTool : IAgentTool
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IFileStorage _storage;

    public UpdateProjectWorkspaceTool(IServiceScopeFactory scopeFactory, IFileStorage storage)
    {
        _scopeFactory = scopeFactory;
        _storage = storage;
    }

    public string Name => "update_project_workspace";

    public string Description =>
        "Create or update an immutable generated-project source ZIP. Supply complete UTF-8 text " +
        "files. To continue editing, pass the latest sourceArchiveFileId returned by this tool.";

    public string ParametersSchema => """
    {
      "type": "object",
      "properties": {
        "projectSlug": { "type": "string" },
        "sourceArchiveFileId": { "type": "string" },
        "files": {
          "type": "array",
          "items": {
            "type": "object",
            "properties": {
              "path": { "type": "string" },
              "content": { "type": "string" },
              "delete": { "type": "boolean", "default": false }
            },
            "required": ["path"]
          }
        }
      },
      "required": ["projectSlug", "files"]
    }
    """;

    public async Task<ToolResult> ExecuteAsync(
        JsonElement arguments,
        ToolContext context,
        CancellationToken ct)
    {
        UpdateProjectWorkspaceRequest? request;
        try
        {
            request = arguments.Deserialize<UpdateProjectWorkspaceRequest>(JsonDefaults.Options);
        }
        catch (JsonException)
        {
            request = null;
        }

        if (request is null || request.Files.Count == 0)
        {
            return ToolResult.Fail("At least one project file change is required.");
        }
        if (!CreateProjectWorkspaceTool.IsValidSlug(request.ProjectSlug))
        {
            return ToolResult.Fail(
                "projectSlug must be 3-24 lowercase letters, numbers, or hyphens and start/end alphanumeric.");
        }
        if (string.IsNullOrWhiteSpace(request.SourceArchiveFileId))
        {
            return ToolResult.Fail(
                "sourceArchiveFileId is required. Create the approved project workspace first.");
        }

        using (var scope = _scopeFactory.CreateScope())
        {
            var messages = scope.ServiceProvider.GetRequiredService<IMessageRepository>();
            var history = await messages.ListAsync(context.ThreadId, ct);
            var approvalError = SoftwareFactoryApprovals.ValidateArchitecture(history);
            if (approvalError is not null)
            {
                return ToolResult.Fail(approvalError);
            }
            var revisionError = SoftwareFactoryApprovals.ValidateLatestSourceRevision(
                history,
                request.ProjectSlug,
                request.SourceArchiveFileId);
            if (revisionError is not null)
            {
                return ToolResult.Fail(revisionError);
            }
        }

        Dictionary<string, string> files;
        try
        {
            files = await LoadSourceFilesAsync(request.SourceArchiveFileId, context.ThreadId, ct);
            foreach (var change in request.Files)
            {
                var path = ProjectArchive.NormalizePath(change.Path);
                if (change.Delete)
                {
                    files.Remove(path);
                }
                else
                {
                    files[path] = change.Content
                        ?? throw new InvalidDataException($"File '{path}' is missing content.");
                }
            }
            ProjectArchive.Validate(files);
        }
        catch (InvalidDataException ex)
        {
            return ToolResult.Fail(ex.Message);
        }

        var archive = ProjectArchive.Create(files);
        var artifact = await _storage.UploadAsync(
            new ArtifactOwner(context.UserId, context.ThreadId),
            $"{request.ProjectSlug}-source.zip",
            ArtifactKind.SourceZip,
            archive,
            "application/zip",
            ct);
        return ToolResult.Ok(
            $"Project workspace updated. sourceArchiveFileId={artifact.Id}; " +
            $"files={files.Count}; sizeBytes={archive.Length}.",
            artifact);
    }

    private async Task<Dictionary<string, string>> LoadSourceFilesAsync(
        string? sourceArchiveFileId,
        string threadId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sourceArchiveFileId))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        using var scope = _scopeFactory.CreateScope();
        var messages = scope.ServiceProvider.GetRequiredService<IMessageRepository>();
        var artifact = await ThreadArtifacts.FindAsync(messages, threadId, sourceArchiveFileId, ct);
        if (artifact is null || artifact.Kind != ArtifactKind.SourceZip)
        {
            throw new InvalidDataException(
                "sourceArchiveFileId must reference a source ZIP from this conversation.");
        }
        var content = await _storage.ReadBytesAsync(artifact.BlobPath, ct)
            ?? throw new InvalidDataException("The source ZIP no longer exists in storage.");
        return ProjectArchive.Read(content);
    }
}

public sealed class ReadProjectWorkspaceTool : IAgentTool
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IFileStorage _storage;

    public ReadProjectWorkspaceTool(IServiceScopeFactory scopeFactory, IFileStorage storage)
    {
        _scopeFactory = scopeFactory;
        _storage = storage;
    }

    public string Name => "read_project_workspace";

    public string Description =>
        "List files or read one UTF-8 text file from a generated source ZIP in this conversation.";

    public string ParametersSchema => """
    {
      "type": "object",
      "properties": {
        "sourceArchiveFileId": { "type": "string" },
        "path": { "type": "string", "description": "Omit to list all files." }
      },
      "required": ["sourceArchiveFileId"]
    }
    """;

    public async Task<ToolResult> ExecuteAsync(
        JsonElement arguments,
        ToolContext context,
        CancellationToken ct)
    {
        if (!arguments.TryGetProperty("sourceArchiveFileId", out var idElement)
            || idElement.GetString() is not { Length: > 0 } fileId)
        {
            return ToolResult.Fail("sourceArchiveFileId is required.");
        }

        using var scope = _scopeFactory.CreateScope();
        var messages = scope.ServiceProvider.GetRequiredService<IMessageRepository>();
        var artifact = await ThreadArtifacts.FindAsync(messages, context.ThreadId, fileId, ct);
        if (artifact is null || artifact.Kind != ArtifactKind.SourceZip)
        {
            return ToolResult.Fail("The source ZIP does not exist in this conversation.");
        }
        var content = await _storage.ReadBytesAsync(artifact.BlobPath, ct);
        if (content is null)
        {
            return ToolResult.Fail("The source ZIP no longer exists in storage.");
        }

        try
        {
            var files = ProjectArchive.Read(content);
            var requestedPath = arguments.TryGetProperty("path", out var pathElement)
                ? pathElement.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(requestedPath))
            {
                return ToolResult.Ok(string.Join("\n", files.Keys.OrderBy(path => path)));
            }

            var path = ProjectArchive.NormalizePath(requestedPath);
            if (!files.TryGetValue(path, out var text))
            {
                return ToolResult.Fail($"File '{path}' was not found in the source ZIP.");
            }
            var truncated = text.Length > 30_000;
            return ToolResult.Ok(truncated ? text[..30_000] + "\n[truncated]" : text);
        }
        catch (InvalidDataException ex)
        {
            return ToolResult.Fail(ex.Message);
        }
    }
}

public sealed class BuildTestProjectRequest
{
    public string ProjectSlug { get; set; } = string.Empty;
    public string SourceArchiveFileId { get; set; } = string.Empty;
    public string SolutionPath { get; set; } = "GeneratedApp.sln";
    public string BackendProjectPath { get; set; } = "src/backend/GeneratedApp.Api.csproj";
    public string FrontendDirectory { get; set; } = "src/frontend";
}

public sealed class BuildTestProjectTool : IAgentTool
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IFileStorage _storage;
    private readonly BuildExecutor _executor;
    private readonly IOptionsMonitor<BuildExecutionOptions> _options;

    public BuildTestProjectTool(
        IServiceScopeFactory scopeFactory,
        IFileStorage storage,
        BuildExecutor executor,
        IOptionsMonitor<BuildExecutionOptions> options)
    {
        _scopeFactory = scopeFactory;
        _storage = storage;
        _executor = executor;
        _options = options;
    }

    public string Name => "build_test_project";

    public string Description =>
        "Build and test an immutable source ZIP in an isolated E2B sandbox using a fixed " +
        "restore, build, .NET tests, frontend tests, frontend build, Playwright E2E, and publish " +
        "pipeline. Successful builds return desktop/mobile screenshots plus deployment packages.";

    public string ParametersSchema => """
    {
      "type": "object",
      "properties": {
        "projectSlug": { "type": "string" },
        "sourceArchiveFileId": { "type": "string" },
        "solutionPath": { "type": "string", "default": "GeneratedApp.sln" },
        "backendProjectPath": { "type": "string", "default": "src/backend/GeneratedApp.Api.csproj" },
        "frontendDirectory": { "type": "string", "default": "src/frontend" }
      },
      "required": ["projectSlug", "sourceArchiveFileId"]
    }
    """;

    public async Task<ToolResult> ExecuteAsync(
        JsonElement arguments,
        ToolContext context,
        CancellationToken ct)
    {
        BuildTestProjectRequest? request;
        try
        {
            request = arguments.Deserialize<BuildTestProjectRequest>(JsonDefaults.Options);
        }
        catch (JsonException)
        {
            request = null;
        }
        if (request is null || string.IsNullOrWhiteSpace(request.SourceArchiveFileId))
        {
            return ToolResult.Fail("A valid sourceArchiveFileId is required.");
        }
        if (!CreateProjectWorkspaceTool.IsValidSlug(request.ProjectSlug))
        {
            return ToolResult.Fail("A valid projectSlug is required.");
        }
        if (!_options.CurrentValue.Enabled)
        {
            return ToolResult.Fail("E2B build execution is disabled by the Cosmos deployment profile.");
        }

        using var scope = _scopeFactory.CreateScope();
        var messages = scope.ServiceProvider.GetRequiredService<IMessageRepository>();
        var history = await messages.ListAsync(context.ThreadId, ct);
        var revisionError = SoftwareFactoryApprovals.ValidateLatestSourceRevision(
            history,
            request.ProjectSlug,
            request.SourceArchiveFileId);
        if (revisionError is not null)
        {
            return ToolResult.Fail(revisionError);
        }
        var source = await ThreadArtifacts.FindAsync(
            messages, context.ThreadId, request.SourceArchiveFileId, ct);
        if (source is null || source.Kind != ArtifactKind.SourceZip)
        {
            return ToolResult.Fail("The source ZIP does not exist in this conversation.");
        }
        var sourceBytes = await _storage.ReadBytesAsync(source.BlobPath, ct);
        if (sourceBytes is null)
        {
            return ToolResult.Fail("The source ZIP no longer exists in storage.");
        }

        try
        {
            var response = await _executor.ExecuteAsync(
                sourceBytes,
                request.SolutionPath,
                request.BackendProjectPath,
                request.FrontendDirectory,
                ct);
            var owner = new ArtifactOwner(context.UserId, context.ThreadId);
            var reportBytes = Encoding.UTF8.GetBytes(BuildReport(response));
            var report = await _storage.UploadAsync(
                owner,
                $"{request.ProjectSlug}-build-report.txt",
                ArtifactKind.BuildReport,
                reportBytes,
                "text/plain; charset=utf-8",
                ct);

            if (!response.Succeeded)
            {
                var failedStep = response.Steps.LastOrDefault(step => !step.Succeeded);
                var diagnostic = failedStep?.Output ?? response.Summary;
                if (diagnostic.Length > 12_000)
                {
                    diagnostic = diagnostic[^12_000..];
                }
                return ToolResult.Fail(
                    $"Build/test failed. buildReportFileId={report.Id}. {response.Summary}\n" +
                    $"Failed-stage output:\n{diagnostic}",
                    new[] { report });
            }

            var backendBytes = DecodePackage(
                response.BackendPackageBase64, "backend", 48 * 1024 * 1024);
            var frontendBytes = DecodePackage(
                response.FrontendPackageBase64, "frontend", 24 * 1024 * 1024);
            var desktopBytes = DecodePackage(
                response.DesktopScreenshotBase64, "desktop UI screenshot", 5 * 1024 * 1024);
            var mobileBytes = DecodePackage(
                response.MobileScreenshotBase64, "mobile UI screenshot", 5 * 1024 * 1024);
            var desktop = await _storage.UploadAsync(
                owner,
                $"{request.ProjectSlug}-ui-desktop.png",
                ArtifactKind.UiScreenshot,
                desktopBytes,
                "image/png",
                ct);
            var mobile = await _storage.UploadAsync(
                owner,
                $"{request.ProjectSlug}-ui-mobile.png",
                ArtifactKind.UiScreenshot,
                mobileBytes,
                "image/png",
                ct);
            var backend = await _storage.UploadAsync(
                owner,
                $"{request.ProjectSlug}-backend.zip",
                ArtifactKind.BackendPackage,
                backendBytes,
                "application/zip",
                ct);
            var frontend = await _storage.UploadAsync(
                owner,
                $"{request.ProjectSlug}-frontend.zip",
                ArtifactKind.FrontendPackage,
                frontendBytes,
                "application/zip",
                ct);

            return ToolResult.Ok(
                $"Build and all tests passed. buildReportFileId={report.Id}; " +
                $"desktopScreenshotFileId={desktop.Id}; mobileScreenshotFileId={mobile.Id}; " +
                $"backendPackageFileId={backend.Id}; frontendPackageFileId={frontend.Id}. " +
                "Show both screenshots to the user and wait for APPROVE UI before Azure preview.",
                new[] { report, desktop, mobile, backend, frontend });
        }
        catch (Exception ex) when (ex is InvalidOperationException
            or InvalidDataException
            or HttpRequestException
            or TaskCanceledException
            or FormatException)
        {
            return ToolResult.Fail(
                $"E2B build execution failed: {ex.Message} " +
                "Verify the E2B API key, template ID, template runner, and E2B service availability.");
        }
    }

    private static byte[] DecodePackage(string? content, string name, int maxBytes)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new InvalidOperationException($"Build execution omitted the {name} package.");
        }
        var bytes = Convert.FromBase64String(content);
        if (bytes.Length == 0 || bytes.Length > maxBytes)
        {
            throw new InvalidDataException(
                $"Build execution returned an invalid {name} size.");
        }
        return bytes;
    }

    private static string BuildReport(BuildProjectResponse response)
    {
        var report = new StringBuilder();
        report.AppendLine(response.Summary);
        if (!string.IsNullOrWhiteSpace(response.BuildId))
        {
            report.AppendLine($"BuildId: {response.BuildId}");
        }
        foreach (var step in response.Steps)
        {
            report.AppendLine();
            report.AppendLine($"## {step.Name}: {(step.Succeeded ? "passed" : "failed")} " +
                $"(exit={step.ExitCode}, durationMs={step.DurationMilliseconds})");
            report.AppendLine(step.Output);
        }
        return report.ToString();
    }
}

internal static class ProjectArchive
{
    private const int MaxFiles = 500;
    private const int MaxFileChars = 512_000;
    private const int MaxTotalChars = 8_000_000;

    public static Dictionary<string, string> Read(byte[] archiveBytes)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        using var stream = new MemoryStream(archiveBytes, writable: false);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        foreach (var entry in archive.Entries.Where(entry => !string.IsNullOrEmpty(entry.Name)))
        {
            var path = NormalizePath(entry.FullName);
            if (entry.Length > MaxFileChars * 4L)
            {
                throw new InvalidDataException($"Source file '{path}' is too large.");
            }
            using var reader = new StreamReader(entry.Open(), Encoding.UTF8, true);
            result[path] = reader.ReadToEnd();
        }
        Validate(result);
        return result;
    }

    public static byte[] Create(IReadOnlyDictionary<string, string> files)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in files.OrderBy(item => item.Key, StringComparer.Ordinal))
            {
                var entry = archive.CreateEntry(file.Key, CompressionLevel.Optimal);
                entry.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
                using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                writer.Write(file.Value);
            }
        }
        return stream.ToArray();
    }

    public static void Validate(IReadOnlyDictionary<string, string> files)
    {
        if (files.Count == 0 || files.Count > MaxFiles)
        {
            throw new InvalidDataException($"A workspace must contain 1-{MaxFiles} text files.");
        }
        var total = 0;
        foreach (var file in files)
        {
            _ = NormalizePath(file.Key);
            if (file.Value.Length > MaxFileChars)
            {
                throw new InvalidDataException($"Source file '{file.Key}' exceeds {MaxFileChars} characters.");
            }
            total = checked(total + file.Value.Length);
        }
        if (total > MaxTotalChars)
        {
            throw new InvalidDataException($"Workspace text exceeds {MaxTotalChars} characters.");
        }
    }

    public static string NormalizePath(string path)
    {
        var normalized = path.Replace('\\', '/').Trim('/');
        var segments = normalized.Split('/');
        if (string.IsNullOrWhiteSpace(normalized)
            || Path.IsPathRooted(path)
            || segments.Any(segment => segment is "" or "." or "..")
            || segments.Any(segment => segment.Equals("node_modules", StringComparison.OrdinalIgnoreCase)
                || segment.Equals("bin", StringComparison.OrdinalIgnoreCase)
                || segment.Equals("obj", StringComparison.OrdinalIgnoreCase)
                || segment.Equals("dist", StringComparison.OrdinalIgnoreCase)
                || segment.Equals(".git", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException($"Project path '{path}' is not allowed.");
        }
        return normalized;
    }
}

internal static class SoftwareFactoryApprovals
{
    public const string ArchitecturePhrase = "APPROVE ARCHITECTURE";
    public const string UiPhrase = "APPROVE UI";

    public static string? ValidateArchitecture(IReadOnlyList<ChatMessage> history)
    {
        var architecture = history.LastOrDefault(message =>
            message.Role == MessageRole.Assistant
            && message.Content.Contains("```mermaid", StringComparison.OrdinalIgnoreCase));
        var latestUser = history.LastOrDefault(message => message.Role == MessageRole.User);
        if (architecture?.Content.Length > 30_000)
        {
            return "The Mermaid architecture proposal is too large. Render a concise diagram before approval.";
        }
        return architecture is not null
               && latestUser is not null
               && latestUser.Sequence > architecture.Sequence
               && string.Equals(
                   latestUser.Content.Trim(), ArchitecturePhrase, StringComparison.Ordinal)
            ? null
            : $"Show a Mermaid architecture diagram and wait for the user to send exactly " +
              $"'{ArchitecturePhrase}' before creating the project workspace.";
    }

    public static string? ValidateLatestSourceRevision(
        IReadOnlyList<ChatMessage> history,
        string projectSlug,
        string sourceArchiveFileId)
    {
        var latest = FindLatestSourceRevision(history, projectSlug);
        if (latest is null)
        {
            return $"No source revision exists for project '{projectSlug}'.";
        }
        return string.Equals(latest.Id, sourceArchiveFileId, StringComparison.OrdinalIgnoreCase)
            ? null
            : $"sourceArchiveFileId is stale. Use the latest revision for project '{projectSlug}': {latest.Id}";
    }

    public static Artifact? FindLatestSourceRevision(
        IReadOnlyList<ChatMessage> history,
        string projectSlug)
    {
        var expectedName = $"{projectSlug}-source.zip";
        return history
            .OrderBy(message => message.Sequence)
            .SelectMany(message => message.Artifacts)
            .LastOrDefault(artifact =>
                artifact.Kind == ArtifactKind.SourceZip
                && string.Equals(artifact.FileName, expectedName, StringComparison.Ordinal));
    }

    public static string? ValidateUi(
        IReadOnlyList<ChatMessage> history,
        ChatMessage buildMessage)
    {
        var screenshots = buildMessage.Artifacts.Count(
            artifact => artifact.Kind == ArtifactKind.UiScreenshot);
        var latestUser = history.LastOrDefault(message => message.Role == MessageRole.User);
        return screenshots >= 2
               && latestUser is not null
               && latestUser.Sequence > buildMessage.Sequence
               && string.Equals(latestUser.Content.Trim(), UiPhrase, StringComparison.Ordinal)
            ? null
            : $"Show the desktop and mobile UI screenshots and wait for the user to send exactly " +
              $"'{UiPhrase}' before Azure preview.";
    }
}