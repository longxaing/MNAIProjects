using System.Text.Json;
using MnaiWork.Api.Models;

namespace MnaiWork.Api.Agent;

internal static class BuildEvidence
{
    internal static bool IsContinuation(IReadOnlyList<ChatMessage> history)
    {
        var content = history.LastOrDefault(message => message.Role == MessageRole.User)?.Content.Trim();
        return content is "继续" or "继续修复" or "continue" or "Continue" or "APPROVE UI"
            || content?.StartsWith("CONTINUE REPAIR ", StringComparison.Ordinal) == true;
    }

    internal static string? GetBlockingReply(IReadOnlyList<ChatMessage> history)
    {
        var source = history.Where(message => message.Role == MessageRole.Tool)
            .OrderBy(message => message.Sequence)
            .SelectMany(message => message.Artifacts
                .Where(artifact => artifact.Kind == ArtifactKind.SourceZip)
                .Select(artifact => (message.Sequence, Artifact: artifact)))
            .LastOrDefault();
        const string suffix = "-source.zip";
        if (source.Artifact is null || !source.Artifact.FileName.EndsWith(suffix, StringComparison.Ordinal)) return null;
        var slug = source.Artifact.FileName[..^suffix.Length];
        var build = history.Where(message => message.Role == MessageRole.Tool && message.ToolName == "build_test_project")
            .OrderBy(message => message.Sequence)
            .LastOrDefault(message => Argument(message, "projectSlug") == slug
                || message.Artifacts.Any(artifact => artifact.Kind == ArtifactKind.BuildReport
                    && artifact.FileName == $"{slug}-build-report.txt"));
        var prefix = $"项目 {slug} 的当前源码尚未通过完整构建验收，不能确认截图或部署包已生成，也不能进入 UI 审批或部署预览。";
        if (build is null || build.Sequence < source.Sequence)
            return prefix + "\n最新源码需要重新执行 build_test_project；修改源码不等于构建成功。";
        if (build.ToolSucceeded != true)
        {
            var diagnostic = build.Content.Length > 2000 ? build.Content[..2000] : build.Content;
            return prefix + "\n最近一次构建失败或缺少明确成功记录。真实工具诊断：\n" + diagnostic;
        }
        if (Argument(build, "sourceArchiveFileId") != source.Artifact.Id)
            return prefix + "\n成功记录没有关联到当前源码版本，需要重新构建确认。";
        if (!HasArtifact(build, ArtifactKind.BackendPackage, $"{slug}-backend.zip")
            || !HasArtifact(build, ArtifactKind.FrontendPackage, $"{slug}-frontend.zip")
            || !HasArtifact(build, ArtifactKind.UiScreenshot, $"{slug}-ui-desktop.png")
            || !HasArtifact(build, ArtifactKind.UiScreenshot, $"{slug}-ui-mobile.png"))
            return prefix + "\n成功记录缺少前后端 ZIP 或桌面、移动端截图，产物不完整。";
        return null;
    }

    private static bool HasArtifact(ChatMessage build, ArtifactKind kind, string name) =>
        build.Artifacts.Any(artifact => artifact.Kind == kind && artifact.FileName == name
            && !string.IsNullOrWhiteSpace(artifact.Id));

    private static string? Argument(ChatMessage message, string name) =>
        message.ToolArguments is { ValueKind: JsonValueKind.Object } arguments
        && arguments.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;
}