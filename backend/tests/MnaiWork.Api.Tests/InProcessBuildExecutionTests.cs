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

        var result = await executor.ExecuteAsync(
            CreateSourceArchive(fixtureRoot),
            "GeneratedApp.sln",
            "src/backend/GeneratedApp.Api.csproj",
            "src/frontend",
            CancellationToken.None);

        Assert.True(
            result.Succeeded,
            result.Summary + Environment.NewLine + string.Join(
                Environment.NewLine,
                result.Steps.Select(step => $"[{step.Name}] {step.Output}")));
        Assert.Equal(9, result.Steps.Count);
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

    private static byte[] CreateSourceArchive(
        string root,
        (string Path, string OldValue, string NewValue)? replacement = null)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(root, file);
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