namespace TorrentEngine.Api.Torrents;

/// <summary>Thrown by <see cref="ITorrentEngine.AddAsync"/> when a torrent with the same info hash is
/// already registered (or an add for it is in flight) — mapped to <c>409 Conflict</c> by the API.</summary>
public sealed class DuplicateTorrentException(string infoHash)
    : InvalidOperationException($"Torrent {infoHash} is already registered.")
{
    public string InfoHash { get; } = infoHash;
}

/// <summary>A file inside a torrent, once the file list is known (immediately for <c>.torrent</c>, after
/// metadata for magnets). <see cref="RelativePath"/> is relative to the torrent's save directory.</summary>
public sealed record TorrentFileInfo(int Index, string RelativePath, long Length);

/// <summary>What is known about a torrent right after it is added.</summary>
public sealed record TorrentDescriptor(
    string InfoHash,
    string? Name,
    long? TotalSize,
    bool HasMetadata,
    IReadOnlyList<TorrentFileInfo> Files);

/// <summary>
/// A live, in-memory progress snapshot (never persisted). The first ten fields are the original
/// contract (unchanged names/order for existing consumers); the rest are additive richer stats.
/// </summary>
/// <param name="Peers">Currently connected peer connections (<c>OpenConnections</c>).</param>
/// <param name="SizeBytes">Total content size, or 0 before a magnet's metadata arrives.</param>
/// <param name="Seeds">Connected peers that have the complete torrent.</param>
/// <param name="Leeches">Connected peers still downloading.</param>
/// <param name="AvailablePeers">Peers known from trackers/DHT/PEX but not currently connected — a
/// high value here with few <see cref="Peers"/> points at a connectivity/NAT (port-forwarding) issue
/// rather than a discovery one.</param>
/// <param name="DownloadedBytes">Payload bytes received this session (resets on restart); basis for
/// <see cref="Ratio"/>, not the same as completed content after a resume.</param>
/// <param name="UploadedBytes">Payload bytes sent this session.</param>
/// <param name="RemainingBytes">Content still to download, derived from completion; 0 when complete.</param>
/// <param name="TotalPieces">Total pieces once metadata is known (0 before).</param>
/// <param name="CompletePieces">Verified pieces already downloaded.</param>
/// <param name="PieceLengthBytes">Size of one piece, or 0 before metadata.</param>
/// <param name="EtaSeconds">Estimated seconds to completion at the current rate; <c>null</c> when
/// complete, stalled (rate 0), or size unknown.</param>
/// <param name="AddedAt">When the torrent was added to the engine this session.</param>
/// <param name="ElapsedSeconds">Seconds since <see cref="AddedAt"/> (server-computed).</param>
public sealed record TorrentSnapshot(
    string InfoHash,
    string? Name,
    string EngineState,
    bool Complete,
    double PercentComplete,
    long DownloadRateBytesPerSecond,
    long UploadRateBytesPerSecond,
    double Ratio,
    int Peers,
    long SizeBytes,
    int Seeds,
    int Leeches,
    int AvailablePeers,
    long DownloadedBytes,
    long UploadedBytes,
    long RemainingBytes,
    int TotalPieces,
    int CompletePieces,
    long PieceLengthBytes,
    long? EtaSeconds,
    DateTimeOffset AddedAt,
    double ElapsedSeconds);

/// <summary>Per-torrent rate limits (bytes/sec; 0 = unlimited).</summary>
public sealed record TorrentLimits(int MaxDownloadRate, int MaxUploadRate);

/// <summary>
/// Thin wrapper over the MonoTorrent <c>ClientEngine</c>. Owns no persistence; surfaces the file
/// list and live snapshots, and raises events for the transitions a consumer cares about. The
/// control API exposes these over HTTP/SSE.
/// </summary>
public interface ITorrentEngine
{
    /// <summary>Parses a source to read its info hash and (for <c>.torrent</c>) size/files, without
    /// adding it to the engine.</summary>
    TorrentDescriptor Inspect(TorrentSource source);

    Task<TorrentDescriptor> AddAsync(TorrentSource source, string saveDirectory, TorrentLimits limits, bool autoStart, CancellationToken cancellationToken);

    Task PauseAsync(string infoHash, CancellationToken cancellationToken);

    Task ResumeAsync(string infoHash, CancellationToken cancellationToken);

    Task StopAsync(string infoHash, CancellationToken cancellationToken);

    Task RemoveAsync(string infoHash, bool deleteFiles, CancellationToken cancellationToken);

    TorrentSnapshot? GetSnapshot(string infoHash);

    IReadOnlyList<TorrentSnapshot> GetAllSnapshots();

    /// <summary>The file list, or <c>null</c> when no torrent with this info hash is registered
    /// (an empty list means the torrent exists but has no metadata yet).</summary>
    IReadOnlyList<TorrentFileInfo>? GetFiles(string infoHash);

    /// <summary>Raised when a magnet's file list becomes available after metadata download.</summary>
    event EventHandler<string>? MetadataReceived;

    /// <summary>Raised when a torrent finishes downloading (transition to a complete/seeding state).</summary>
    event EventHandler<string>? DownloadCompleted;

    /// <summary>Raised when a torrent enters an error state.</summary>
    event EventHandler<string>? DownloadErrored;
}
