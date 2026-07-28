using System.Threading.Channels;

namespace SeekClaw.Runtime.Events;

/// <summary>
/// In-process publish/subscribe bus. Producers (agent, tools, providers) publish;
/// renderers and other observers subscribe. Publishing never blocks.
/// </summary>
public interface IEventBus
{
    void Publish(RuntimeEvent evt);

    /// <summary>Creates an independent subscription with its own unbounded queue.</summary>
    IEventSubscription Subscribe();
}

public interface IEventSubscription : IDisposable
{
    ChannelReader<RuntimeEvent> Reader { get; }
}

public sealed class EventBus : IEventBus
{
    private readonly Lock _gate = new();
    private readonly List<Subscription> _subscriptions = [];

    public void Publish(RuntimeEvent evt)
    {
        lock (_gate)
        {
            foreach (var sub in _subscriptions)
                sub.Channel.Writer.TryWrite(evt);
        }
    }

    public IEventSubscription Subscribe()
    {
        var sub = new Subscription(this);
        lock (_gate)
            _subscriptions.Add(sub);
        return sub;
    }

    private void Remove(Subscription sub)
    {
        lock (_gate)
            _subscriptions.Remove(sub);
    }

    private sealed class Subscription(EventBus owner) : IEventSubscription
    {
        public Channel<RuntimeEvent> Channel { get; } =
            System.Threading.Channels.Channel.CreateUnbounded<RuntimeEvent>(
                new UnboundedChannelOptions { SingleReader = true });

        public ChannelReader<RuntimeEvent> Reader => Channel.Reader;

        public void Dispose()
        {
            owner.Remove(this);
            Channel.Writer.TryComplete();
        }
    }
}
