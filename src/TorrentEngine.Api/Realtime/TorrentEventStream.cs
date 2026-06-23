using System.Collections.Concurrent;
using System.Threading.Channels;
using TorrentEngine.Api.Torrents;
using TorrentEngine.Api.Vpn;

namespace TorrentEngine.Api.Realtime;

/// <summary>One event on the control SSE stream.</summary>
/// <param name="Type"><c>progress</c> | <c>metadata-received</c> | <c>completed</c> | <c>errored</c> | <c>vpn</c>.</param>
/// <param name="InfoHash">The torrent's info hash; empty for engine-wide events such as <c>vpn</c>.</param>
/// <param name="Snapshot">Torrent snapshot for per-torrent events; <c>null</c> otherwise.</param>
/// <param name="Vpn">VPN tunnel status for the <c>vpn</c> event; <c>null</c> otherwise.</param>
public sealed record TorrentEvent(string Type, string InfoHash, TorrentSnapshot? Snapshot, VpnStatus? Vpn = null);

/// <summary>
/// Fan-out hub for torrent events: <see cref="TorrentProgressBroadcaster"/> publishes, each
/// <c>GET /events</c> subscriber drains its own bounded channel (slow readers drop oldest).
/// </summary>
public sealed class TorrentEventStream
{
    private readonly ConcurrentDictionary<Guid, Channel<TorrentEvent>> _subscribers = new();

    public (Guid Id, ChannelReader<TorrentEvent> Reader) Subscribe()
    {
        var channel = Channel.CreateBounded<TorrentEvent>(
            new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        var id = Guid.NewGuid();
        _subscribers[id] = channel;
        return (id, channel.Reader);
    }

    public void Unsubscribe(Guid id)
    {
        if (_subscribers.TryRemove(id, out var channel))
        {
            channel.Writer.TryComplete();
        }
    }

    public void Publish(TorrentEvent evt)
    {
        foreach (var channel in _subscribers.Values)
        {
            channel.Writer.TryWrite(evt);
        }
    }
}
