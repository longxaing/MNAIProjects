using System.Text;
using System.Text.Json;
using MnaiWork.Api.Agent.Skills;
using MnaiWork.Api.Agent.Tools;
using MnaiWork.Api.Configuration;
using MnaiWork.Api.Data;
using MnaiWork.Api.Models;
using Microsoft.Extensions.Options;
using OpenAI.Responses;
using MessageRole = MnaiWork.Api.Models.MessageRole;

namespace MnaiWork.Api.Agent;

/// <summary>
/// Executes one agent run: streams the model response, invokes tools, persists every message to
/// Cosmos and publishes live events to the SSE bus. This is the "background service" that talks to
/// OpenAI on behalf of a chat turn.
/// </summary>
public sealed class AgentRunner
{
    private readonly ResponsesClient _client;
    private readonly ContextManager _context;
    private readonly IMessageRepository _messages;
    private readonly IRunRepository _runs;
    private readonly IAgentEventBus _bus;
    private readonly ToolRegistry _tools;
    private readonly AgentSkillRegistry _skills;
    private readonly AzureOpenAiOptions _options;
    private readonly ILogger<AgentRunner> _logger;

    public AgentRunner(
        ResponsesClient client,
        ContextManager context,
        IMessageRepository messages,
        IRunRepository runs,
        IAgentEventBus bus,
        ToolRegistry tools,
        AgentSkillRegistry skills,
        IOptions<AzureOpenAiOptions> options,
        ILogger<AgentRunner> logger)
    {
        _client = client;
        _context = context;
        _messages = messages;
        _runs = runs;
        _bus = bus;
        _tools = tools;
        _skills = skills;
        _options = options.Value;
        _logger = logger;
    }

    public async Task RunAsync(AgentRunRequest request, CancellationToken ct)
    {
        var (runId, threadId, userId) = (request.RunId, request.ThreadId, request.UserId);
        long eventSeq = 0;

        void Emit(AgentEvent evt) => _bus.Publish(runId, evt with { Seq = Interlocked.Increment(ref eventSeq), RunId = runId });

        var run = await _runs.GetAsync(threadId, runId, ct);
        if (run is null)
        {
            _logger.LogWarning("Run {RunId} not found; skipping.", runId);
            return;
        }

        try
        {
            run.Status = RunStatus.Running;
            await _runs.UpsertAsync(run, ct);
            Emit(new AgentEvent { Type = "run", Status = "running" });

            var history = await _messages.ListAsync(threadId, ct);
            long seq = (history.Count > 0 ? history.Max(m => m.Sequence) : 0) + 1;

            if (AgentRunWorkflow.GetDeploymentApprovalSlug(history) is not null)
            {
                var arguments = AgentRunWorkflow.GetApprovedDeploymentArguments(history);
                ToolResult deploymentResult;
                if (arguments is null)
                {
                    deploymentResult = ToolResult.Fail(
                        "Deployment was not started. No matching successful preview with persisted arguments " +
                        "was found. Send APPROVE UI to obtain a fresh preview, then confirm its exact DEPLOY phrase.");
                }
                else if (!_skills.TryLoad(runId, "software-factory", out _))
                {
                    deploymentResult = ToolResult.Fail("Deployment was not started: software-factory skill is unavailable.");
                }
                else
                {
                    deploymentResult = await ExecuteToolCoreAsync(
                        "deploy_azure_project", arguments, threadId, userId, runId, seq++, Emit, ct);
                }

                var reply = new ChatMessage
                {
                    ThreadId = threadId,
                    RunId = runId,
                    Role = MessageRole.Assistant,
                    Sequence = seq++,
                    Content = deploymentResult.Output,
                    Streaming = false
                };
                await _messages.AddAsync(reply, ct);
                Emit(new AgentEvent { Type = "message", MessageId = reply.Id, Role = "assistant" });
                Emit(new AgentEvent { Type = "message_done", MessageId = reply.Id, Content = reply.Content });
                run.Status = deploymentResult.Success ? RunStatus.Completed : RunStatus.Failed;
                run.Error = deploymentResult.Success ? null : deploymentResult.Output;
                await _runs.UpsertAsync(run, ct);
                Emit(new AgentEvent { Type = "run", Status = deploymentResult.Success ? "completed" : "failed", Error = run.Error });
                return;
            }

            // Keep the prompt bounded: recent turns verbatim, older turns summarized.
            var prepared = await _context.PrepareAsync(history, ct);
            var input = prepared.Items;
            var instructions = prepared.Summary is null
                ? SystemPrompts.Agent
                : $"{SystemPrompts.Agent}\n\n## Summary of earlier conversation\n{prepared.Summary}";
            instructions = $"{instructions}\n\n{_skills.BuildCatalogPrompt()}";
            var deploymentContinuation = AgentRunWorkflow.BuildDeploymentContinuationPrompt(history);
            if (deploymentContinuation is not null)
            {
                instructions = $"{instructions}\n\n{deploymentContinuation}";
            }
            var repairContinuation = AgentRunWorkflow.BuildRepairContinuationPrompt(history);
            if (repairContinuation is not null)
            {
                instructions = $"{instructions}\n\n{repairContinuation}";
            }

            var reachedFinalResponse = false;
            var repairRequired = false;
            string? previousFailureSignature = null;
            var repeatedFailureCount = 0;
            for (var iteration = 0; iteration < _options.MaxToolIterations; iteration++)
            {
                ct.ThrowIfCancellationRequested();

                var options = new CreateResponseOptions
                {
                    Model = _options.Deployment,
                    Instructions = instructions,
                    StreamingEnabled = true
                };
                foreach (var item in input)
                {
                    options.InputItems.Add(item);
                }
                foreach (var tool in _tools.All.Where(tool => _skills.IsToolAvailable(runId, tool.Name)))
                {
                    options.Tools.Add(ResponseTool.CreateFunctionTool(
                        functionName: tool.Name,
                        functionParameters: BinaryData.FromString(tool.ParametersSchema),
                        strictModeEnabled: false,
                        functionDescription: tool.Description));
                }

                var assistant = new ChatMessage
                {
                    ThreadId = threadId,
                    RunId = runId,
                    Role = MessageRole.Assistant,
                    Sequence = seq++,
                    Streaming = true
                };
                await _messages.AddAsync(assistant, ct);
                Emit(new AgentEvent { Type = "message", MessageId = assistant.Id, Role = "assistant" });

                var text = new StringBuilder();
                ResponseResult? final = null;
                var lastPersistedLength = 0;

                await foreach (var update in _client.CreateResponseStreamingAsync(options, ct))
                {
                    switch (update)
                    {
                        case StreamingResponseOutputTextDeltaUpdate delta:
                            text.Append(delta.Delta);
                            Emit(new AgentEvent { Type = "delta", MessageId = assistant.Id, Delta = delta.Delta });
                            if (text.Length - lastPersistedLength >= 400)
                            {
                                assistant.Content = text.ToString();
                                await _messages.UpsertAsync(assistant, ct);
                                lastPersistedLength = text.Length;
                            }
                            break;

                        case StreamingResponseCompletedUpdate completed:
                            final = completed.Response;
                            break;
                    }
                }

                var assistantText = text.Length > 0 ? text.ToString() : (final?.GetOutputText() ?? string.Empty);
                assistant.Content = assistantText;
                assistant.Streaming = false;
                await _messages.UpsertAsync(assistant, ct);
                Emit(new AgentEvent { Type = "message_done", MessageId = assistant.Id, Content = assistantText });

                var calls = new List<FunctionCallResponseItem>();
                if (final is not null)
                {
                    foreach (var item in final.OutputItems)
                    {
                        input.Add(item); // carry the assistant message + any tool calls into the next turn
                        if (item is FunctionCallResponseItem call)
                        {
                            calls.Add(call);
                        }
                    }
                }
                else if (!string.IsNullOrWhiteSpace(assistantText))
                {
                    input.Add(ResponseItem.CreateAssistantMessageItem(assistantText));
                }

                if (calls.Count == 0)
                {
                    if (repairRequired)
                    {
                        input.Add(ResponseItem.CreateUserMessageItem(
                            AgentRunWorkflow.RepairContinuationPrompt));
                        _logger.LogWarning(
                            "Agent run {RunId} attempted to stop while a repairable build failure " +
                            "was pending; continuing the tool loop.",
                            runId);
                        continue;
                    }
                    reachedFinalResponse = true;
                    break; // model produced a final answer with no tool use
                }

                foreach (var call in calls)
                {
                    var result = await ExecuteToolAsync(
                        call, threadId, userId, runId, seq++, input, Emit, ct);
                    if (string.Equals(
                            call.FunctionName, "build_test_project", StringComparison.Ordinal))
                    {
                        repairRequired = AgentRunWorkflow.RequiresCodeRepair(result);
                        if (repairRequired)
                        {
                            var signature = AgentRunWorkflow.GetFailureSignature(result.Output);
                            repeatedFailureCount = string.Equals(
                                signature, previousFailureSignature, StringComparison.Ordinal)
                                ? repeatedFailureCount + 1
                                : 1;
                            previousFailureSignature = signature;
                            if (repeatedFailureCount >= 2)
                            {
                                repairRequired = false;
                                _logger.LogWarning(
                                    "Agent run {RunId} produced the same build failure signature " +
                                    "twice; automatic repair will stop after the model reports evidence.",
                                    runId);
                            }
                        }
                        else
                        {
                            previousFailureSignature = null;
                            repeatedFailureCount = 0;
                        }
                    }
                }
            }

            if (!reachedFinalResponse)
            {
                _logger.LogWarning(
                    "Agent run {RunId} reached the maximum of {MaxToolIterations} tool iterations.",
                    runId,
                    _options.MaxToolIterations);
                throw new InvalidOperationException(
                    $"Agent reached the maximum of {_options.MaxToolIterations} tool iterations " +
                    "before producing a final response.");
            }

            run.Status = RunStatus.Completed;
            await _runs.UpsertAsync(run, ct);
            Emit(new AgentEvent { Type = "run", Status = "completed" });
        }
        catch (OperationCanceledException)
        {
            run.Status = RunStatus.Canceled;
            await SafeUpdateRunAsync(run);
            Emit(new AgentEvent { Type = "run", Status = "canceled" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Agent run {RunId} failed.", runId);
            run.Status = RunStatus.Failed;
            run.Error = ex.Message;
            await SafeUpdateRunAsync(run);
            Emit(new AgentEvent { Type = "error", Error = ex.Message });
            Emit(new AgentEvent { Type = "run", Status = "failed", Error = ex.Message });
        }
        finally
        {
            _skills.ClearRun(runId);
            Emit(new AgentEvent { Type = "done" });
            _bus.Complete(runId);
        }
    }

    private async Task<ToolResult> ExecuteToolAsync(
        FunctionCallResponseItem call, string threadId, string userId, string runId, long sequence,
        List<ResponseItem> input, Action<AgentEvent> emit, CancellationToken ct)
    {
        var result = await ExecuteToolCoreAsync(call.FunctionName, TryParseArguments(call.FunctionArguments),
            threadId, userId, runId, sequence, emit, ct);
        input.Add(ResponseItem.CreateFunctionCallOutputItem(call.CallId, result.Output));
        return result;
    }

    private async Task<ToolResult> ExecuteToolCoreAsync(
        string toolName, JsonElement? args, string threadId, string userId, string runId, long sequence,
        Action<AgentEvent> emit, CancellationToken ct)
    {
        emit(new AgentEvent { Type = "tool", Tool = toolName, ToolStatus = "started" });

        ToolResult result;
        if (args is null)
        {
            result = ToolResult.Fail($"Could not parse arguments for tool '{toolName}'.");
        }
        else if (!_skills.IsToolAvailable(runId, toolName))
        {
            result = ToolResult.Fail(
                $"Tool '{toolName}' is unavailable until its server-side skill is loaded.");
        }
        else if (_tools.TryGet(toolName, out var tool))
        {
            result = await tool.ExecuteAsync(args.Value, new ToolContext(threadId, userId, runId), ct);
        }
        else
        {
            result = ToolResult.Fail($"Unknown tool '{toolName}'.");
        }

        var toolMessage = new ChatMessage
        {
            ThreadId = threadId,
            RunId = runId,
            Role = MessageRole.Tool,
            ToolName = toolName,
            ToolArguments = toolName is "preview_azure_project" or "deploy_azure_project" ? args?.Clone() : null,
            ToolSucceeded = result.Success,
            Content = result.Output,
            Sequence = sequence
        };
        if (result.Artifacts.Count > 0)
        {
            toolMessage.Artifacts.AddRange(result.Artifacts);
        }
        await _messages.AddAsync(toolMessage, ct);

        emit(new AgentEvent
        {
            Type = "tool",
            MessageId = toolMessage.Id,
            Tool = toolName,
            ToolStatus = result.Success ? "completed" : "failed",
            Summary = result.Output
        });
        foreach (var artifact in result.Artifacts)
        {
            emit(new AgentEvent { Type = "artifact", MessageId = toolMessage.Id, Artifact = artifact });
        }

        return result;
    }

    private static JsonElement? TryParseArguments(BinaryData arguments)
    {
        try
        {
            var raw = arguments?.ToString();
            if (string.IsNullOrWhiteSpace(raw))
            {
                using var empty = JsonDocument.Parse("{}");
                return empty.RootElement.Clone();
            }
            using var doc = JsonDocument.Parse(raw);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task SafeUpdateRunAsync(AgentRun run)
    {
        try
        {
            await _runs.UpsertAsync(run);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist terminal status for run {RunId}.", run.Id);
        }
    }
}

internal static class AgentRunWorkflow
{
    public static string? GetDeploymentApprovalSlug(IReadOnlyList<ChatMessage> history)
    {
        var content = history.LastOrDefault(message => message.Role == MessageRole.User)?.Content.Trim();
        const string prefix = "DEPLOY ";
        return content is not null && content.StartsWith(prefix, StringComparison.Ordinal)
            && CreateProjectWorkspaceTool.IsValidSlug(content[prefix.Length..])
                ? content[prefix.Length..]
                : null;
    }

    public static JsonElement? GetApprovedDeploymentArguments(IReadOnlyList<ChatMessage> history)
    {
        var slug = GetDeploymentApprovalSlug(history);
        var latestUser = history.LastOrDefault(message => message.Role == MessageRole.User);
        if (slug is null || latestUser is null)
        {
            return null;
        }
        var preview = history.LastOrDefault(message => message.Role == MessageRole.Tool
            && message.ToolName == "preview_azure_project" && message.Sequence < latestUser.Sequence);
        if (preview?.ToolSucceeded != true || preview.ToolArguments is not { ValueKind: JsonValueKind.Object } arguments
            || !arguments.TryGetProperty("projectSlug", out var projectSlug)
            || projectSlug.ValueKind != JsonValueKind.String || projectSlug.GetString() != slug)
        {
            return null;
        }
        return arguments.Clone();
    }

    public const string RepairContinuationPrompt = """
        SERVER WORKFLOW: The latest build_test_project call returned a repairable build or test
        failure with a BuildReport. Do not stop, summarize, ask the user to diagnose it, redraw the
        architecture, or request another approval. Inspect the diagnostic and latest SourceZip,
        update all implicated product and test files together, then call build_test_project again.
        For compiler errors involving Program or a missing API namespace, inspect Program.cs and the
        failing integration test together; reference the entry-point type exactly as declared and do
        not infer a namespace from the project or assembly name. Top-level Program is commonly global.
        Continue until the pipeline passes, the same failure repeats without progress, no safe repair
        remains, or the server iteration budget is exhausted.
        """;

    public static bool RequiresCodeRepair(ToolResult result) =>
        !result.Success
        && result.Artifacts.Any(artifact => artifact.Kind == ArtifactKind.BuildReport);

    public static string? BuildRepairContinuationPrompt(IReadOnlyList<ChatMessage> history)
    {
        var latestUser = history.LastOrDefault(message => message.Role == MessageRole.User);
        const string prefix = "CONTINUE REPAIR ";
        if (latestUser is null
            || !latestUser.Content.StartsWith(prefix, StringComparison.Ordinal)
            || !CreateProjectWorkspaceTool.IsValidSlug(latestUser.Content[prefix.Length..].Trim()))
        {
            return null;
        }

        var projectSlug = latestUser.Content[prefix.Length..].Trim();
        var reportFileName = $"{projectSlug}-build-report.txt";
        var sourceFileName = $"{projectSlug}-source.zip";
        var failedBuild = history.LastOrDefault(message =>
            message.Role == MessageRole.Tool
            && string.Equals(message.ToolName, "build_test_project", StringComparison.OrdinalIgnoreCase)
            && message.Artifacts.Any(artifact =>
                artifact.Kind == ArtifactKind.BuildReport
                && string.Equals(artifact.FileName, reportFileName, StringComparison.Ordinal))
            && !message.Artifacts.Any(artifact => artifact.Kind == ArtifactKind.BackendPackage));
        var source = history
            .SelectMany(message => message.Artifacts.Select(artifact => (message.Sequence, Artifact: artifact)))
            .LastOrDefault(item =>
                item.Artifact.Kind == ArtifactKind.SourceZip
                && string.Equals(item.Artifact.FileName, sourceFileName, StringComparison.Ordinal));
        var report = failedBuild?.Artifacts.LastOrDefault(artifact =>
            artifact.Kind == ArtifactKind.BuildReport
            && string.Equals(artifact.FileName, reportFileName, StringComparison.Ordinal));
        if (failedBuild is null
            || report is null
            || source.Artifact is null
            || latestUser.Sequence <= failedBuild.Sequence)
        {
            return null;
        }

        return $"""
            SERVER WORKFLOW: Continue the approved repair for projectSlug={projectSlug}. Load the
            software-factory skill. The latest immutable source is sourceArchiveFileId={source.Artifact.Id}
            and the latest failed build report is buildReportFileId={report.Id}. The persisted failed
            build diagnostic follows:

            {failedBuild.Content}

            Read all implicated files in one read_project_workspace call using paths, apply one batched
            update_project_workspace revision, then call build_test_project. Do not rediscover these IDs,
            redraw the architecture, ask for approval, or provide manual build instructions.
            """;
    }

    public static string? BuildDeploymentContinuationPrompt(IReadOnlyList<ChatMessage> history)
    {
        var latestUser = history.LastOrDefault(message => message.Role == MessageRole.User);
        if (latestUser is null || !string.Equals(
                latestUser.Content.Trim(),
                SoftwareFactoryApprovals.UiPhrase,
                StringComparison.Ordinal))
        {
            return null;
        }

        var build = history.LastOrDefault(message =>
            message.Role == MessageRole.Tool
            && string.Equals(message.ToolName, "build_test_project", StringComparison.OrdinalIgnoreCase));
        var backend = build?.Artifacts.LastOrDefault(
            artifact => artifact.Kind == ArtifactKind.BackendPackage);
        var frontend = build?.Artifacts.LastOrDefault(
            artifact => artifact.Kind == ArtifactKind.FrontendPackage);
        if (build is null
            || backend is null
            || frontend is null
            || build.Artifacts.Count(artifact => artifact.Kind == ArtifactKind.UiScreenshot) < 2
            || latestUser.Sequence <= build.Sequence)
        {
            return null;
        }

        var suffix = "-backend.zip";
        if (!backend.FileName.EndsWith(suffix, StringComparison.Ordinal))
        {
            return null;
        }
        var projectSlug = backend.FileName[..^suffix.Length];
        if (!string.Equals(
                frontend.FileName,
                $"{projectSlug}-frontend.zip",
                StringComparison.Ordinal))
        {
            return null;
        }

        return $"""
            SERVER WORKFLOW: The user has approved the latest successfully tested UI. Load the
            software-factory skill, then call preview_azure_project with projectSlug={projectSlug},
            backendPackageFileId={backend.Id}, and frontendPackageFileId={frontend.Id}. These IDs come
            from the same persisted successful build_test_project call. Do not rebuild, claim the IDs
            are unavailable, ask the user to build ZIP files, or provide manual deployment steps.
            """;
    }

    public static string GetFailureSignature(string output)
    {
        const string startMarker = "failureSignature:";
        const string endMarker = "failedStageDiagnostic:";
        var start = output.IndexOf(startMarker, StringComparison.Ordinal);
        if (start < 0)
        {
            return output;
        }
        start += startMarker.Length;
        var end = output.IndexOf(endMarker, start, StringComparison.Ordinal);
        return (end < 0 ? output[start..] : output[start..end]).Trim();
    }
}
