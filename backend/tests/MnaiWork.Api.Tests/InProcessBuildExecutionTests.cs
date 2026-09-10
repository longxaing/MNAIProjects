using System.IO.Compression;
using MnaiWork.BuildExecution;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MnaiWork.Api.Tests;

public sealed class InProcessBuildExecutionTests
{
    private static readonly HashSet<string> ExcludedDirectories = new(
        new[]
        {
            "node_modules", "bin", "obj", "dist", "TestResults", "test-results",
            "playwright-report", ".git"
        },
        StringComparer.OrdinalIgnoreCase);

    [Fact]
    public async Task ExecuteAsync_BuildsTestsAndPackagesInsideCurrentProcess()
    {
        var backendRoot = FindBackendRoot();
        var fixtureRoot = Path.Combine(backendRoot, "tests", "BuildWorkerFixture");
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BuildExecution:Enabled"] = "true",
                ["BuildExecution:MaxConcurrentBuilds"] = "1",
                ["BuildExecution:CommandTimeoutMinutes"] = "10",
                ["BuildExecution:TotalTimeoutMinutes"] = "30",
                ["BuildExecution:PlaywrightVersion"] = "1.62.1"
            })
            .Build();
        var executor = new LocalBuildPipeline(
            configuration,
            NullLogger<LocalBuildPipeline>.Instance);

        var result = await executor.ExecuteAsync(new BuildProjectRequest(
            Convert.ToBase64String(CreateSourceArchive(fixtureRoot)),
            "GeneratedApp.sln",
            "src/backend/GeneratedApp.Api.csproj",
            "src/frontend",
            PreferredFirstStage: "frontend tests"),
            CancellationToken.None);

        Assert.True(
            result.Succeeded,
            result.Summary + Environment.NewLine + string.Join(
                Environment.NewLine,
                result.Steps.Select(step => $"[{step.Name}] {step.Output}")));
        Assert.Equal(9, result.Steps.Count);
        Assert.Equal("npm ci", result.Steps[0].Name);
        Assert.Equal("frontend tests", result.Steps[1].Name);
        Assert.All(result.Steps, step => Assert.True(step.Succeeded, step.Output));
        Assert.NotEmpty(Convert.FromBase64String(result.BackendPackageBase64!));
        Assert.NotEmpty(Convert.FromBase64String(result.FrontendPackageBase64!));
        Assert.True(Convert.FromBase64String(result.DesktopScreenshotBase64!).Length > 1_000);
        Assert.True(Convert.FromBase64String(result.MobileScreenshotBase64!).Length > 1_000);
    }

    [Fact]
    public async Task ExecuteAsync_RejectsBackendWithoutDefaultAzureCredential()
    {
        var backendRoot = FindBackendRoot();
        var fixtureRoot = Path.Combine(backendRoot, "tests", "BuildWorkerFixture");
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BuildExecution:Enabled"] = "true",
                ["BuildExecution:PlaywrightVersion"] = "1.62.1"
            })
            .Build();
        var executor = new LocalBuildPipeline(
            configuration,
            NullLogger<LocalBuildPipeline>.Instance);
        var archive = CreateSourceArchive(
            fixtureRoot,
            ("src/backend/Program.cs", "DefaultAzureCredential", "EnvironmentCredential"));

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => executor.ExecuteAsync(
            archive,
            "GeneratedApp.sln",
            "src/backend/GeneratedApp.Api.csproj",
            "src/frontend",
            CancellationToken.None));

        Assert.Contains("DefaultAzureCredential", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsync_RejectsMissingNewtonsoftEvenWhenProjectDisablesCheck()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BuildExecution:Enabled"] = "true"
            }).Build();
        var executor = new LocalBuildPipeline(configuration, NullLogger<LocalBuildPipeline>.Instance);
        var archive = CreateSourceArchive(
            Path.Combine(FindBackendRoot(), "tests", "BuildWorkerFixture"),
            ("src/backend/GeneratedApp.Api.csproj",
                "<PackageReference Include=\"Newtonsoft.Json\" Version=\"13.0.4\" />",
                "</ItemGroup><PropertyGroup><AzureCosmosDisableNewtonsoftJsonCheck>true</AzureCosmosDisableNewtonsoftJsonCheck></PropertyGroup><ItemGroup>"));

        var result = await executor.ExecuteAsync(
            archive, "GeneratedApp.sln", "src/backend/GeneratedApp.Api.csproj",
            "src/frontend", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("dotnet restore", result.Steps[0].Name);
        Assert.True(result.Steps[0].Succeeded, result.Steps[0].Output);
        var failed = Assert.Single(result.Steps, step => !step.Succeeded);
        Assert.Equal("dotnet build", failed.Name);
        Assert.Contains("Newtonsoft.Json package must be explicitly referenced", failed.Output, StringComparison.Ordinal);
        Assert.Null(result.BackendPackageBase64);
    }

    [Theory]
    [InlineData("backend.unit", "GeneratedApp.UnitTests", "Microsoft.NET.Test.Sdk")]
    [InlineData("backend.integration", "GeneratedApp.IntegrationTests", "Microsoft.NET.Test.Sdk")]
    [InlineData("backend.unit", "GeneratedApp.UnitTests", "xunit")]
    [InlineData("backend.unit", "GeneratedApp.UnitTests", "xunit.runner.visualstudio")]
    public async Task ExecuteAsync_ReportsMissingTestPackagesAsRepairableConfigurationFailure(
        string directory, string projectName, string packageName)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BuildExecution:Enabled"] = "true"
            }).Build();
        var executor = new LocalBuildPipeline(configuration, NullLogger<LocalBuildPipeline>.Instance);
        var projectPath = $"tests/{directory}/{projectName}.csproj";
        var archive = CreateSourceArchive(
            Path.Combine(FindBackendRoot(), "tests", "BuildWorkerFixture"),
            (projectPath, $"Include=\"{packageName}\"", $"Update=\"{packageName}\""));

        var result = await executor.ExecuteAsync(
            archive, "GeneratedApp.sln", "src/backend/GeneratedApp.Api.csproj",
            "src/frontend", CancellationToken.None);

        Assert.False(result.Succeeded);
        var step = Assert.Single(result.Steps);
        Assert.Equal("backend test configuration", step.Name);
        Assert.Contains(projectPath, step.Output, StringComparison.Ordinal);
        Assert.Contains($"missing required PackageReference '{packageName}'", step.Output, StringComparison.Ordinal);
        Assert.Contains("rerun build_test_project", step.Output, StringComparison.Ordinal);
        Assert.Null(result.BackendPackageBase64);
    }

    [Theory]
    [InlineData("src/frontend/index.html", false)]
    [InlineData("src/frontend/index.html", true)]
    [InlineData("src/frontend/public/runtime-config.js", true)]
    [InlineData("src/frontend/src/runtime-config.ts", true)]
    public async Task ExecuteAsync_ReportsFrontendConfigurationAsRepairableFailure(string path, bool delete)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BuildExecution:Enabled"] = "true",
                ["BuildExecution:PlaywrightVersion"] = "1.62.1"
            }).Build();
        var executor = new LocalBuildPipeline(configuration, NullLogger<LocalBuildPipeline>.Instance);
        var archive = CreateSourceArchive(
            Path.Combine(FindBackendRoot(), "tests", "BuildWorkerFixture"),
            (path, "/runtime-config.js", "/missing-config.js"),
            delete ? path : null);

        var result = await executor.ExecuteAsync(
            archive, "GeneratedApp.sln", "src/backend/GeneratedApp.Api.csproj",
            "src/frontend", CancellationToken.None);

        Assert.False(result.Succeeded);
        var step = Assert.Single(result.Steps);
        Assert.Equal("frontend runtime configuration", step.Name);
        if (path.EndsWith(".ts", StringComparison.Ordinal))
        {
            Assert.Contains("src/frontend/src: missing __APP_CONFIG__", step.Output, StringComparison.Ordinal);
            Assert.Contains("src/frontend/src: missing apiBaseUrl", step.Output, StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains(path + ": missing", step.Output, StringComparison.Ordinal);
        }
        Assert.Contains("read_project_workspace", step.Output, StringComparison.Ordinal);
        Assert.Contains("latest SourceZip", step.Output, StringComparison.Ordinal);
        Assert.Contains("update_project_workspace", step.Output, StringComparison.Ordinal);
        Assert.Contains("rerun build_test_project", step.Output, StringComparison.Ordinal);
        Assert.Contains("Do not stop after promising a repair or request another architecture approval", step.Output, StringComparison.Ordinal);
        Assert.Null(result.BackendPackageBase64);
        Assert.Null(result.FrontendPackageBase64);
        var diagnostic = MnaiWork.Api.Agent.Tools.BuildFailureDiagnostics.Create(result, "report-id");
        Assert.Contains(path.EndsWith(".ts", StringComparison.Ordinal) ? "src/frontend/src" : path,
            diagnostic, StringComparison.Ordinal);
        Assert.True(MnaiWork.Api.Agent.AgentRunWorkflow.RequiresCodeRepair(
            MnaiWork.Api.Agent.Tools.ToolResult.Fail(diagnostic, new[]
            {
                new MnaiWork.Api.Models.Artifact { Kind = MnaiWork.Api.Models.ArtifactKind.BuildReport }
            })));
    }

    private static byte[] CreateSourceArchive(
        string root,
        (string Path, string OldValue, string NewValue)? replacement = null,
        string? deletedPath = null)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(root, file);
                if (string.Equals(relative.Replace('\\', '/'), deletedPath, StringComparison.Ordinal))
                {
                    continue;
                }
                if (relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .Any(ExcludedDirectories.Contains))
                {
                    continue;
                }

                var entry = archive.CreateEntry(relative.Replace('\\', '/'), CompressionLevel.Optimal);
                using var source = File.OpenRead(file);
                using var target = entry.Open();
                if (replacement is { } change
                    && string.Equals(
                        relative.Replace('\\', '/'),
                        change.Path,
                        StringComparison.Ordinal))
                {
                    using var reader = new StreamReader(source);
                    using var writer = new StreamWriter(target);
                    writer.Write(reader.ReadToEnd().Replace(
                        change.OldValue,
                        change.NewValue,
                        StringComparison.Ordinal));
                }
                else
                {
                    source.CopyTo(target);
                }
            }
        }
        return output.ToArray();
    }

    private static string FindBackendRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "MnaiWork.sln")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Unable to locate backend/MnaiWork.sln.");
    }
}