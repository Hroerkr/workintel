using System.Runtime.CompilerServices;
using System.Threading.Channels;
using WorkIntel.Contracts.V1;

namespace WorkIntel.Api.Services;

/// <summary>
/// In-process fan-out for task events. The <see cref="TaskService"/> publishes
/// after every mutation; <c>StreamTaskEvents</c> subscribers receive a copy.
/// </summary>
/// <remarks>
/// Phase 1 lives entirely in-process — fine for a single API instance. When we
/// scale to multiple replicas this needs to become a real pub-sub (Postgres
/// LISTEN/NOTIFY is the cheapest realistic upgrade; Redis Streams / NATS if
/// the topology grows).
/// </remarks>
public sealed class TaskEventBus
{
    private readonly object _lock = new();
    private readonly List<Channel<TaskEvent>> _subscribers = new();

    public void Publish(TaskEvent evt)
    {
        Channel<TaskEvent>[] snapshot;
        lock (_lock) snapshot = _subscribers.ToArray();

        foreach (var ch in snapshot)
        {
            // Bounded channel + DropOldest means a slow subscriber loses old
            // events rather than blocking the publisher.
            ch.Writer.TryWrite(evt);
        }
    }

    public async IAsyncEnumerable<TaskEvent> Subscribe([EnumeratorCancellation] CancellationToken ct)
    {
        var channel = Channel.CreateBounded<TaskEvent>(new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });

        lock (_lock) _subscribers.Add(channel);

        try
        {
            await foreach (var evt in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                yield return evt;
        }
        finally
        {
            lock (_lock) _subscribers.Remove(channel);
            channel.Writer.TryComplete();
        }
    }

    /// <summary>Subscriber count, exposed for tests/diagnostics.</summary>
    public int SubscriberCount
    {
        get { lock (_lock) return _subscribers.Count; }
    }
}
