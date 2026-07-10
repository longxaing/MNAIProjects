using System.Threading.Channels;

namespace MnaiWork.Api.Agent;

public sealed record AgentRunRequest(string RunId, string ThreadId, string UserId);

/// <summary>Hands run requests from the HTTP request thread to the background worker.</summary>
public interface IAgentRunQueue
{
    ValueTask EnqueueAsync(AgentRunRequest request, CancellationToken ct = default);
    IAsyncEnumerable<AgentRunRequest> DequeueAllAsync(CancellationToken ct);
}

public sealed class AgentRunQueue : IAgentRunQueue
{
    private readonly Channel<AgentRunRequest> _channel =
        Channel.CreateUnbounded<AgentRunRequest>(new UnboundedChannelOptions { SingleReader = true });

    public ValueTask EnqueueAsync(AgentRunRequest request, CancellationToken ct = default)
        => _channel.Writer.WriteAsync(request, ct);

    public IAsyncEnumerable<AgentRunRequest> DequeueAllAsync(CancellationToken ct)
        => _channel.Reader.ReadAllAsync(ct);
}
