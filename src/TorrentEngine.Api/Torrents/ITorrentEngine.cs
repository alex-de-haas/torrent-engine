namespace TorrentEngine.Api.Torrents;

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

/// <summary>A live, in-memory progress snapshot (never persisted).</summary>
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
    long SizeBytes);

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

    IReadOnlyList<TorrentFileInfo> GetFiles(string infoHash);

    /// <summary>Raised when a magnet's file list becomes available after metadata download.</summary>
    event EventHandler<string>? MetadataReceived;

    /// <summary>Raised when a torrent finishes downloading (transition to a complete/seeding state).</summary>
    event EventHandler<string>? DownloadCompleted;

    /// <summary>Raised when a torrent enters an error state.</summary>
    event EventHandler<string>? DownloadErrored;
}
