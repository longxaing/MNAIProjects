namespace MnaiWork.Api.Models;

public sealed record CreateThreadRequest(string? Title);

public sealed record SendMessageRequest(string Content, List<Attachment>? Attachments = null);

public sealed record SendMessageResponse(string RunId, string ThreadId, string UserMessageId);

public sealed record ThreadListItem(string Id, string Title, DateTimeOffset UpdatedAt);
