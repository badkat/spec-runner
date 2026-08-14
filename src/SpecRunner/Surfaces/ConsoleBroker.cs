using System.Threading.Channels;

namespace SpecRunner.Surfaces;

/// <summary>
/// The browser console surface, server side.
///
/// Feature 8.3 - reconnecting the browser displays the whole run log, not just subsequent
/// events. A refresh mid-run must not produce a console showing half the story. Subscription
/// therefore hands back a history snapshot and a live channel taken together under one lock, so
/// a subscriber can neither miss an event nor see one twice.
/// </summary>
public sealed class ConsoleBroker
{
    private readonly object _gate = new();
    private readonly List<EmittedEvent> _history = [];
    private readonly List<Channel<EmittedEvent>> _subscribers = [];

    public void Publish(EmittedEvent e)
    {
        List<Channel<EmittedEvent>> targets;
        lock (_gate)
        {
            // Token events are display-only (feature 8.5) and would otherwise dominate the
            // replay history for a long run. The assembled text is published as one durable
            // event when the call completes, so a late-joining browser still sees what the
            // model said - it just does not re-watch it arrive.
            if (!e.TransientDisplayOnly)
            {
                _history.Add(e);
            }

            targets = [.. _subscribers];
        }

        foreach (var subscriber in targets)
        {
            // Unbounded channels: a slow browser must never apply backpressure to the workflow.
            subscriber.Writer.TryWrite(e);
        }
    }

    public (IReadOnlyList<EmittedEvent> History, ChannelReader<EmittedEvent> Live) Subscribe()
    {
        var channel = Channel.CreateUnbounded<EmittedEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        lock (_gate)
        {
            _subscribers.Add(channel);
            return ([.. _history], channel.Reader);
        }
    }

    public void Unsubscribe(ChannelReader<EmittedEvent> reader)
    {
        lock (_gate)
        {
            var index = _subscribers.FindIndex(c => ReferenceEquals(c.Reader, reader));
            if (index >= 0)
            {
                _subscribers[index].Writer.TryComplete();
                _subscribers.RemoveAt(index);
            }
        }
    }

    public IReadOnlyList<EmittedEvent> History
    {
        get
        {
            lock (_gate)
            {
                return [.. _history];
            }
        }
    }
}
