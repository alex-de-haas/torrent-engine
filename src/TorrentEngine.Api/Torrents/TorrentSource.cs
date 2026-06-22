namespace TorrentEngine.Api.Torrents;

/// <summary>How a torrent enters the engine: a magnet URI (file list deferred until metadata) or a
/// <c>.torrent</c> file (file list known immediately).</summary>
public abstract record TorrentSource
{
    public sealed record Magnet(string Uri) : TorrentSource;

    public sealed record File(byte[] Content, string? FileName) : TorrentSource;
}
