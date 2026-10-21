using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Wharf.Api;

/// <summary>
/// Fan-out for SSE clients. Events carry only the new cursor; clients always
/// re-read the manifest, so a dropped event cannot lose data.
/// </summary>
public sealed class ChangeNotifier
{
    private readonly ConcurrentDictionary<Guid, Channel<long>> _subscribers = new();

    public Subscription Subscribe()
    {
        var id = Guid.NewGuid();
        var channel = Channel.CreateBounded<long>(new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });

        _subscribers[id] = channel;
        return new Subscription(this, id, channel.Reader);
    }

    public void Publish(long seq)
    {
        foreach (var channel in _subscribers.Values)
            channel.Writer.TryWrite(seq);
    }

    private void Unsubscribe(Guid id)
    {
        if (_subscribers.TryRemove(id, out var channel))
            channel.Writer.TryComplete();
    }

    public sealed class Subscription : IDisposable
    {
        private readonly ChangeNotifier _owner;
        private readonly Guid _id;

        internal Subscription(ChangeNotifier owner, Guid id, ChannelReader<long> reader)
        {
            _owner = owner;
            _id = id;
            Reader = reader;
        }

        public ChannelReader<long> Reader { get; }

        public void Dispose() => _owner.Unsubscribe(_id);
    }
}
