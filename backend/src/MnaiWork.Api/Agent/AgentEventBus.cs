using System.Collections.Concurrent;
using System.Threading.Channels;

namespace MnaiWork.Api.Agent;

/// <summary>A live subscription to a run's event stream.</summary>
public sealed class AgentEventSubscription : IDisposable
{
    private readonly Action _dispose;
    public AgentEventSubscription(ChannelReader<AgentEvent> reader, Action dispose)
    {
        Reader = reader;
        _dispose = dispose;
    }

    public ChannelReader<AgentEvent> Reader { get; }
    public void Dispose() => _dispose();
}

/// <summary>
/// In-process fan-out of run events to any connected SSE readers. Keyed by run id.
/// (Single-instance design — swap for Redis pub/sub to scale horizontally.)
/// </summary>
public interface IAgentEventBus
{
    void Publish(string runId, AgentEvent evt);
    AgentEventSubscription Subscribe(string runId);
    void Complete(string runId);
    bool IsActive(string runId);
}

public sealed class AgentEventBus : IAgentEventBus
{
    private sealed class RunChannel
    {
        public readonly ConcurrentDictionary<Guid, Channel<AgentEvent>> Subscribers = new();
        public volatile bool Completed;
    }

    private readonly ConcurrentDictionary<string, RunChannel> _runs = new();

    public void Publish(string runId, AgentEvent evt)
    {
        if (_runs.TryGetValue(runId, out var run))
        {
            foreach (var channel in run.Subscribers.Values)
            {
                channel.Writer.TryWrite(evt);
            }
        }
    }

    public AgentEventSubscription Subscribe(string runId)
    {
        var run = _runs.GetOrAdd(runId, _ => new RunChannel());
        var channel = Channel.CreateUnbounded<AgentEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
        var id = Guid.NewGuid();
        run.Subscribers[id] = channel;

        if (run.Completed)
        {
            channel.Writer.TryComplete();
        }

        return new AgentEventSubscription(channel.Reader, () =>
        {
            if (run.Subscribers.TryRemove(id, out var removed))
            {
                removed.Writer.TryComplete();
            }
        });
    }

    public void Complete(string runId)
    {
        if (_runs.TryGetValue(runId, out var run))
        {
            run.Completed = true;
            foreach (var channel in run.Subscribers.Values)
            {
                channel.Writer.TryComplete();
            }
            // Give late-joining readers a short grace period, then drop the run.
            _ = Task.Delay(TimeSpan.FromMinutes(2)).ContinueWith(t => _runs.TryRemove(runId, out _));
        }
    }

    public bool IsActive(string runId) => _runs.TryGetValue(runId, out var run) && !run.Completed;
}
