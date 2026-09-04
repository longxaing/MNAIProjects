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

            // Keep the prompt bounded: recent turns verbatim, older turns summarized.
            var prepared = await _context.PrepareAsync(history, ct);
            var input = prepared.Items;
            var instructions = prepared.Summary is null
                ? SystemPrompts.Agent
                : $"{SystemPrompts.Agent}\n\n## Summary of earlier conversation\n{prepared.Summary}";
            instructions = $"{instructions}\n\n{_skills.BuildCatalogPrompt()}";

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
                    break; // model produced a final answer with no tool use
                }

                foreach (var call in calls)
                {
                    await ExecuteToolAsync(call, threadId, userId, runId, seq++, input, Emit, ct);
                }
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

    private async Task ExecuteToolAsync(
        FunctionCallResponseItem call, string threadId, string userId, string runId, long sequence,
        List<ResponseItem> input, Action<AgentEvent> emit, CancellationToken ct)
    {
        emit(new AgentEvent { Type = "tool", Tool = call.FunctionName, ToolStatus = "started" });

        ToolResult result;
        JsonElement? args = TryParseArguments(call.FunctionArguments);
        if (args is null)
        {
            result = ToolResult.Fail($"Could not parse arguments for tool '{call.FunctionName}'.");
        }
        else if (!_skills.IsToolAvailable(runId, call.FunctionName))
        {
            result = ToolResult.Fail(
                $"Tool '{call.FunctionName}' is unavailable until its server-side skill is loaded.");
        }
        else if (_tools.TryGet(call.FunctionName, out var tool))
        {
            result = await tool.ExecuteAsync(args.Value, new ToolContext(threadId, userId, runId), ct);
        }
        else
        {
            result = ToolResult.Fail($"Unknown tool '{call.FunctionName}'.");
        }

        var toolMessage = new ChatMessage
        {
            ThreadId = threadId,
            RunId = runId,
            Role = MessageRole.Tool,
            ToolName = call.FunctionName,
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
            Tool = call.FunctionName,
            ToolStatus = result.Success ? "completed" : "failed",
            Summary = result.Output
        });
        foreach (var artifact in result.Artifacts)
        {
            emit(new AgentEvent { Type = "artifact", MessageId = toolMessage.Id, Artifact = artifact });
        }

        input.Add(ResponseItem.CreateFunctionCallOutputItem(call.CallId, result.Output));
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
