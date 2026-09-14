using System.Text.RegularExpressions;
using MnaiWork.Api.Agent.Tools;
using MnaiWork.Api.Models;

namespace MnaiWork.Api.Agent;

internal static class ArchitectureHandoff
{
    private static readonly Regex Diagram = new(
        @"```mermaid\s*\r?\n\s*flowchart\s+(?:LR|TD)\b[^`]+```",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(200));

    internal const string ImplementApproved = "SERVER ARCHITECTURE HANDOFF: The latest user has already approved the displayed architecture. " +
        "Load software-factory if needed, then create the project workspace or inspect/update the existing latest source and continue implementation. " +
        "Do not redraw the architecture, request APPROVE ARCHITECTURE again, or end with a promise to start later. " +
        "Do not invent a new architecture or bypass other approval gates. If a tool fails, report or repair its real failure.";

    internal static bool IsApprovalTurn(IReadOnlyList<ChatMessage> history) =>
        history.LastOrDefault(message => message.Role == MessageRole.User)?.Content.Trim()
            == SoftwareFactoryApprovals.ArchitecturePhrase;

    internal static bool HasApprovedArchitecture(IReadOnlyList<ChatMessage> history) =>
        IsApprovalTurn(history) && SoftwareFactoryApprovals.ValidateArchitecture(history) is null;

    internal static string? GetCorrection(string text, bool approved, bool awaitingImplementation, bool hasCalls)
    {
        if (approved && (text.Contains(SoftwareFactoryApprovals.ArchitecturePhrase, StringComparison.Ordinal)
            || Diagram.IsMatch(text) || awaitingImplementation && !hasCalls))
            return ImplementApproved;

        if (!approved && text.Contains(SoftwareFactoryApprovals.ArchitecturePhrase, StringComparison.Ordinal)
            && !Diagram.IsMatch(text))
            return "SERVER ARCHITECTURE HANDOFF: Do not request approval before showing the proposal. " +
                "Provide the complete Mermaid flowchart in this response (closed mermaid fence, flowchart LR or TD), " +
                "briefly explain the design, then request exact APPROVE ARCHITECTURE and stop. " +
                "A prior approval sent before this diagram does not authorize source changes. Do not call implementation tools yet.";
        return null;
    }
}