using System.Text;
using MnaiWork.Api.Configuration;
using MnaiWork.Api.Models;
using Microsoft.Extensions.Options;
using Microsoft.ML.Tokenizers;
using OpenAI.Responses;
using MessageRole = MnaiWork.Api.Models.MessageRole;

namespace MnaiWork.Api.Agent;

/// <summary>The conversation prepared for the model: recent turns verbatim + an optional recap.</summary>
public sealed record PreparedContext(string? Summary, List<ResponseItem> Items);

/// <summary>
/// Keeps the model input within a bounded size to avoid context-window overflow. Recent turns are
/// sent verbatim; older turns are condensed into a short summary (or trimmed if summarization is
/// unavailable), so long conversations never blow up the prompt.
/// </summary>
public sealed class ContextManager
{
    private readonly ResponsesClient _client;
    private readonly AzureOpenAiOptions _options;
    private readonly ILogger<ContextManager> _logger;

    public ContextManager(ResponsesClient client, IOptions<AzureOpenAiOptions> options, ILogger<ContextManager> logger)
    {
        _client = client;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<PreparedContext> PrepareAsync(IReadOnlyList<ChatMessage> history, CancellationToken ct)
    {
        // Only user + assistant messages form the cross-turn conversation carried into a new run.
        var turns = history
            .Where(m => m.Role == MessageRole.User ||
                        (m.Role == MessageRole.Assistant && !string.IsNullOrWhiteSpace(m.Content)))
            .ToList();

        var totalTokens = turns.Sum(m => EstimateTokens(m.Content));
        if (totalTokens <= _options.MaxContextTokens || turns.Count <= _options.MinRecentTurns)
        {
            return new PreparedContext(null, turns.Select(ToInputItem).ToList());
        }

        // Keep the newest turns within a budget; reserve space for the approved architecture that
        // must remain verbatim as the implementation contract.
        var architecture = FindLatestArchitecture(turns);
        var architectureTokens = architecture is null ? 0 : EstimateTokens(architecture.Content);
        var recentBudget = Math.Max(0, _options.RecentContextTokens - architectureTokens);
        var recent = new List<ChatMessage>();
        var recentTokens = 0;
        for (var i = turns.Count - 1; i >= 0; i--)
        {
            if (turns[i].Id == architecture?.Id)
            {
                continue;
            }
            var tokens = EstimateTokens(turns[i].Content);
            var haveMinimum = recent.Count >= _options.MinRecentTurns;
            if (haveMinimum && recentTokens + tokens > recentBudget)
            {
                break;
            }
            recent.Insert(0, turns[i]);
            recentTokens += tokens;
        }
        while (recent.Count > 1 && recentTokens > recentBudget)
        {
            recentTokens -= EstimateTokens(recent[0].Content);
            recent.RemoveAt(0);
        }

        var pinnedArchitecture = architecture;
        var recentIds = recent.Select(message => message.Id).ToHashSet(StringComparer.Ordinal);
        var older = turns.Where(message =>
                !recentIds.Contains(message.Id)
                && message.Id != pinnedArchitecture?.Id)
            .ToList();
        var summary = await SummarizeAsync(older, ct);
        var items = recent.Select(ToInputItem).ToList();
        if (pinnedArchitecture is not null)
        {
            items.Insert(0, ToInputItem(pinnedArchitecture));
        }

        _logger.LogInformation(
            "Context compacted: {Older} older turn(s) summarized, {Recent} kept verbatim (~{Tokens} tokens).",
            older.Count, recent.Count, recentTokens);

        return new PreparedContext(summary, items);
    }

    internal static ChatMessage? FindLatestArchitecture(IEnumerable<ChatMessage> messages) =>
        messages.LastOrDefault(message =>
            message.Role == MessageRole.Assistant
            && message.Content.Contains("```mermaid", StringComparison.OrdinalIgnoreCase));

    private async Task<string?> SummarizeAsync(IReadOnlyList<ChatMessage> older, CancellationToken ct)
    {
        if (older.Count == 0)
        {
            return null;
        }

        var transcript = new StringBuilder();
        foreach (var m in older)
        {
            transcript.AppendLine($"{(m.Role == MessageRole.User ? "User" : "Assistant")}: {m.Content}");
        }

        var prompt =
            "Summarize the earlier part of a conversation with a document and software-factory assistant. " +
            "Preserve concrete facts needed to continue: project slug, requirements, accepted architecture " +
            "decisions, API/data contracts, implemented features, approval state, latest source revision and " +
            "deployment identifiers mentioned by the assistant, deployed URLs, document titles, formats, " +
            "themes, audience, key content points, and files already produced. " +
            "Write a compact summary (max ~200 words).\n\nConversation:\n" + transcript;

        try
        {
            var options = new CreateResponseOptions { Model = _options.Deployment };
            options.InputItems.Add(ResponseItem.CreateUserMessageItem(prompt));
            ResponseResult response = await _client.CreateResponseAsync(options, ct);
            var text = response.GetOutputText();
            return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        }
        catch (Exception ex)
        {
            // On failure we simply drop the older turns (trim). Safe — just less prior context.
            _logger.LogWarning(ex, "Context summarization failed; trimming older turns instead.");
            return null;
        }
    }

    private static ResponseItem ToInputItem(ChatMessage message)
    {
        var text = message.Content;
        if (message.Role == MessageRole.User && message.Attachments.Count > 0)
        {
            // Make uploaded files discoverable to the model: list id, kind and name so it can call
            // read_attachment (for docs) or reference imageId (for images) in the generate tools.
            var lines = message.Attachments.Select(a =>
                $"  - id={a.Id} | kind={a.Kind.ToString().ToLowerInvariant()} | name={a.FileName}");
            var note = "[Attached files]\n" + string.Join("\n", lines);
            text = string.IsNullOrWhiteSpace(text) ? note : $"{text}\n\n{note}";
        }

        return message.Role == MessageRole.User
            ? ResponseItem.CreateUserMessageItem(text)
            : ResponseItem.CreateAssistantMessageItem(text);
    }

    // Accurate token counting via the o200k_base BPE encoder (used by the gpt-4o / gpt-5 family),
    // so budgeting is correct for Chinese, code and mixed text — not just English prose.
    private static readonly Tokenizer? Tokenizer = CreateTokenizer();

    private static Tokenizer? CreateTokenizer()
    {
        try
        {
            return TiktokenTokenizer.CreateForModel("gpt-4o");
        }
        catch
        {
            return null; // Fall back to the heuristic below if the vocab can't be loaded.
        }
    }

    private static int EstimateTokens(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }
        // Real BPE count when available; otherwise a conservative char-based heuristic.
        return Tokenizer?.CountTokens(text) ?? text.Length / 3 + 1;
    }
}
