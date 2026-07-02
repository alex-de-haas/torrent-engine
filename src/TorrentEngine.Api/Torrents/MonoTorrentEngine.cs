using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using MonoTorrent;
using MonoTorrent.Client;
using MonoTorrent.Connections;

namespace TorrentEngine.Api.Torrents;

/// <summary>
/// MonoTorrent-backed <see cref="ITorrentEngine"/> and hosted service. Owns the <see cref="ClientEngine"/>,
/// enables DHT/PEX/LSD and protocol encryption, binds the configured raw torrent port, and persists
/// fast-resume/metadata under the app data dir so downloads survive restarts.
/// </summary>
public sealed class MonoTorrentEngine : ITorrentEngine, IHostedService, IDisposable
{
    private readonly TorrentEngineSettings _settings;
    private readonly ILogger<MonoTorrentEngine> _logger;
    private readonly ConcurrentDictionary<string, TorrentManager> _managers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _completionRaised = new(StringComparer.OrdinalIgnoreCase);
    // When each torrent was added this session — feeds the snapshot's AddedAt/ElapsedSeconds. MonoTorrent
    // does not track this itself, and (like the Monitor's byte counters) it is session-scoped by design.
    private readonly ConcurrentDictionary<string, DateTimeOffset> _addedAt = new(StringComparer.OrdinalIgnoreCase);

    private ClientEngine? _engine;

    public MonoTorrentEngine(TorrentEngineSettings settings, ILogger<MonoTorrentEngine> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    public event EventHandler<string>? MetadataReceived;
    public event EventHandler<string>? DownloadCompleted;
    public event EventHandler<string>? DownloadErrored;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var port = _settings.Port;
        var bindAddress = TryParseBindAddress(_settings.BindAddress);
        var cacheDirectory = Path.Combine(_settings.AppDataDir, "torrent-engine");
        Directory.CreateDirectory(cacheDirectory);
        foreach (var root in _settings.DownloadsRoots.Values)
        {
            Directory.CreateDirectory(root);
        }

        var builder = new EngineSettingsBuilder
        {
            CacheDirectory = cacheDirectory,
            AllowPortForwarding = _settings.EnablePortMapping,
            AllowLocalPeerDiscovery = true,
            AutoSaveLoadFastResume = true,
            AutoSaveLoadMagnetLinkMetadata = true,
            AllowedEncryption = [EncryptionType.RC4Header, EncryptionType.RC4Full, EncryptionType.PlainText],
            MaximumDownloadRate = _settings.MaxDownloadSpeed,
            MaximumUploadRate = _settings.MaxUploadSpeed,
            ListenEndPoints = BuildListenEndPoints(bindAddress, port),
            DhtEndPoint = new IPEndPoint(bindAddress ?? IPAddress.Any, port),
        };

        _engine = new ClientEngine(builder.ToSettings());
        _logger.LogInformation("Torrent engine started on port {Port} (port mapping: {PortMapping}).", port, _settings.EnablePortMapping);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_engine is not null)
        {
            try
            {
                await _engine.StopAllAsync(TimeSpan.FromSeconds(10));
                await _engine.SaveStateAsync();
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Error while stopping the torrent engine.");
            }
        }
    }

    public TorrentDescriptor Inspect(TorrentSource source)
    {
        switch (source)
        {
            case TorrentSource.Magnet magnet:
            {
                if (!MagnetLink.TryParse(magnet.Uri, out var link))
                {
                    throw new ArgumentException("Invalid magnet link.", nameof(source));
                }

                return new TorrentDescriptor(HashOf(link.InfoHashes), link.Name, link.Size, HasMetadata: false, []);
            }

            case TorrentSource.File file:
            {
                var torrent = Torrent.Load(file.Content.AsSpan());
                var files = MapFiles(torrent.Files, torrent.Name);
                return new TorrentDescriptor(HashOf(torrent.InfoHashes), torrent.Name, torrent.Size, HasMetadata: true, files);
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(source));
        }
    }

    public async Task<TorrentDescriptor> AddAsync(
        TorrentSource source, string saveDirectory, TorrentLimits limits, bool autoStart, CancellationToken cancellationToken)
    {
        var engine = RequireEngine();
        Directory.CreateDirectory(saveDirectory);

        var torrentSettings = new TorrentSettingsBuilder
        {
            AllowDht = true,
            AllowPeerExchange = true,
            CreateContainingDirectory = true,
            MaximumDownloadRate = limits.MaxDownloadRate,
            MaximumUploadRate = limits.MaxUploadRate,
        }.ToSettings();

        TorrentManager manager;
        TorrentDescriptor descriptor;

        switch (source)
        {
            case TorrentSource.Magnet magnet:
            {
                if (!MagnetLink.TryParse(magnet.Uri, out var link))
                {
                    throw new ArgumentException("Invalid magnet link.", nameof(source));
                }

                manager = await engine.AddAsync(link, saveDirectory, torrentSettings);
                descriptor = new TorrentDescriptor(HashOf(link.InfoHashes), link.Name, link.Size, HasMetadata: false, []);
                break;
            }

            case TorrentSource.File file:
            {
                var torrent = await Torrent.LoadAsync(file.Content.AsMemory());
                manager = await engine.AddAsync(torrent, saveDirectory, torrentSettings);
                descriptor = new TorrentDescriptor(
                    HashOf(torrent.InfoHashes), torrent.Name, torrent.Size, HasMetadata: true, MapFiles(torrent.Files, torrent.Name));
                break;
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(source));
        }

        // Record the add time before exposing the manager, so any snapshot that observes the torrent also
        // observes its AddedAt. TryAdd (not indexer assignment) so a value a racing snapshot already
        // stabilized via AddedAtOf's GetOrAdd is not clobbered with a later timestamp.
        _addedAt.TryAdd(descriptor.InfoHash, DateTimeOffset.UtcNow);
        _managers[descriptor.InfoHash] = manager;
        manager.TorrentStateChanged += OnTorrentStateChanged;

        if (autoStart)
        {
            await manager.StartAsync();
        }

        if (!descriptor.HasMetadata)
        {
            _ = WaitForMetadataAsync(descriptor.InfoHash, manager);
        }
        else
        {
            RaiseMetadata(descriptor.InfoHash);
        }

        return descriptor;
    }

    public async Task PauseAsync(string infoHash, CancellationToken cancellationToken)
    {
        if (_managers.TryGetValue(infoHash, out var manager))
        {
            await manager.PauseAsync();
        }
    }

    public async Task ResumeAsync(string infoHash, CancellationToken cancellationToken)
    {
        if (_managers.TryGetValue(infoHash, out var manager))
        {
            await manager.StartAsync();
        }
    }

    public async Task StopAsync(string infoHash, CancellationToken cancellationToken)
    {
        if (_managers.TryGetValue(infoHash, out var manager))
        {
            await manager.StopAsync();
        }
    }

    public async Task RemoveAsync(string infoHash, bool deleteFiles, CancellationToken cancellationToken)
    {
        var engine = RequireEngine();
        if (!_managers.TryGetValue(infoHash, out var manager))
        {
            DeleteResumeData(infoHash);
            _completionRaised.TryRemove(infoHash, out _);
            _addedAt.TryRemove(infoHash, out _);
            return;
        }

        try
        {
            if (manager.State is not (TorrentState.Stopped or TorrentState.Stopping or TorrentState.Error))
            {
                await manager.StopAsync(TimeSpan.FromSeconds(10));
            }
        }
        catch (Exception exception)
        {
            // Removal must proceed regardless (ObjectDisposed/InvalidOperation/Canceled etc.).
            _logger.LogWarning(exception, "Stopping torrent {InfoHash} before removal failed; removing anyway.", infoHash);
        }

        var mode = (deleteFiles ? RemoveMode.DownloadedDataOnly : RemoveMode.KeepAllData) | RemoveMode.CacheDataOnly;
        await engine.RemoveAsync(manager, mode);

        _managers.TryRemove(infoHash, out _);
        manager.TorrentStateChanged -= OnTorrentStateChanged;
        _completionRaised.TryRemove(infoHash, out _);
        _addedAt.TryRemove(infoHash, out _);

        DeleteResumeData(infoHash);
    }

    /// <summary>Deletes the persisted fast-resume file for an info hash, if present.</summary>
    private void DeleteResumeData(string infoHash)
    {
        try
        {
            var engineCache = Path.Combine(_settings.AppDataDir, "torrent-engine");
            if (!Directory.Exists(engineCache))
            {
                return;
            }

            // MonoTorrent's fast-resume subdirectory name/casing is not guaranteed across platforms or
            // versions, so match it case-insensitively (its absence on a case-sensitive Linux FS would
            // otherwise skip cleanup), and match files by info-hash regardless of extension.
            var fastResumeDir = Directory.EnumerateDirectories(engineCache)
                .FirstOrDefault(dir => string.Equals(Path.GetFileName(dir), "fastresume", StringComparison.OrdinalIgnoreCase));
            if (fastResumeDir is null)
            {
                return;
            }

            foreach (var file in Directory.EnumerateFiles(fastResumeDir))
            {
                if (string.Equals(Path.GetFileNameWithoutExtension(file), infoHash, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(file);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "Failed to clear fast-resume for {InfoHash}.", infoHash);
        }
    }

    public TorrentSnapshot? GetSnapshot(string infoHash) =>
        _managers.TryGetValue(infoHash, out var manager) ? ToSnapshot(infoHash, manager, AddedAtOf(infoHash)) : null;

    public IReadOnlyList<TorrentSnapshot> GetAllSnapshots() =>
        _managers.Select(pair => ToSnapshot(pair.Key, pair.Value, AddedAtOf(pair.Key))).ToList();

    // GetOrAdd so a snapshot that races ahead of AddAsync's TryAdd still gets a stable timestamp for the
    // rest of the session, rather than a fresh UtcNow on every call. Only ever called for a hash that is in
    // _managers (and cleaned up alongside it in RemoveAsync), so this never leaks stray entries.
    private DateTimeOffset AddedAtOf(string infoHash) =>
        _addedAt.GetOrAdd(infoHash, static _ => DateTimeOffset.UtcNow);

    public IReadOnlyList<TorrentFileInfo>? GetFiles(string infoHash) =>
        _managers.TryGetValue(infoHash, out var manager)
            ? (manager.HasMetadata ? MapManagerFiles(manager) : [])
            : null;

    private async Task WaitForMetadataAsync(string infoHash, TorrentManager manager)
    {
        try
        {
            await manager.WaitForMetadataAsync();
            RaiseMetadata(infoHash);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed waiting for metadata of {InfoHash}.", infoHash);
        }
    }

    private void RaiseMetadata(string infoHash) => MetadataReceived?.Invoke(this, infoHash);

    private void OnTorrentStateChanged(object? sender, TorrentStateChangedEventArgs args)
    {
        if (sender is not TorrentManager manager)
        {
            return;
        }

        var infoHash = HashOf(manager.InfoHashes);

        if (args.NewState == TorrentState.Error)
        {
            DownloadErrored?.Invoke(this, infoHash);
            return;
        }

        // MonoTorrent transitions Downloading → Seeding the moment a torrent completes; a freshly
        // re-added complete torrent also lands in Seeding after hashing. Raise completion once.
        if ((args.NewState == TorrentState.Seeding || manager.Complete) && _completionRaised.TryAdd(infoHash, 0))
        {
            DownloadCompleted?.Invoke(this, infoHash);
        }
    }

    private static TorrentSnapshot ToSnapshot(string infoHash, TorrentManager manager, DateTimeOffset addedAt)
    {
        var monitor = manager.Monitor;
        var downloaded = monitor.DataBytesReceived;
        var uploaded = monitor.DataBytesSent;
        var ratio = downloaded > 0 ? Math.Round(uploaded / (double)downloaded, 3) : 0;
        var size = manager.Torrent?.Size ?? manager.MagnetLink?.Size ?? 0;

        // Progress is 0..100 (Bitfield.PercentComplete). Derive remaining content from it rather than from
        // the session byte counter, which diverges from completed content after a resume. Pin remaining to 0
        // once complete so floating-point rounding in the progress product never leaves a stray byte.
        var progress = manager.Progress;
        var completedBytes = size > 0 ? (long)(size * (progress / 100.0)) : 0;
        var remaining = manager.Complete ? 0 : Math.Max(0, size - completedBytes);

        var downloadRate = monitor.DownloadRate;
        long? etaSeconds = !manager.Complete && downloadRate > 0 && remaining > 0
            ? (long)Math.Ceiling(remaining / (double)downloadRate)
            : null;

        // Piece stats are meaningful only once metadata is known: a metadata-less magnet carries a
        // placeholder 1-bit bitfield, so gate on Torrent to report 0/0 pre-metadata (the documented
        // contract). The null-conditional is defensive — Bitfield is constructor-initialized in
        // MonoTorrent 3.0.2, but a throw here would sink the whole GetAllSnapshots() batch.
        var hasMetadata = manager.Torrent is not null;
        var bitfield = manager.Bitfield;
        var totalPieces = hasMetadata ? bitfield?.Length ?? 0 : 0;
        var completePieces = hasMetadata ? bitfield?.TrueCount ?? 0 : 0;
        var peers = manager.Peers;
        var elapsed = Math.Max(0, (DateTimeOffset.UtcNow - addedAt).TotalSeconds);

        return new TorrentSnapshot(
            infoHash,
            manager.Name,
            manager.State.ToString(),
            manager.Complete,
            Math.Round(progress, 2),
            downloadRate,
            monitor.UploadRate,
            ratio,
            manager.OpenConnections,
            size,
            peers.Seeds,
            peers.Leechs,
            peers.Available,
            downloaded,
            uploaded,
            remaining,
            totalPieces,
            completePieces,
            manager.Torrent?.PieceLength ?? 0,
            etaSeconds,
            addedAt,
            Math.Round(elapsed, 1));
    }

    private static IReadOnlyList<TorrentFileInfo> MapManagerFiles(TorrentManager manager)
    {
        var files = new List<TorrentFileInfo>(manager.Files.Count);
        for (var index = 0; index < manager.Files.Count; index++)
        {
            var file = manager.Files[index];
            var relative = NormalizeRelative(Path.GetRelativePath(manager.SavePath, file.FullPath));
            files.Add(new TorrentFileInfo(index, relative, file.Length));
        }

        return files;
    }

    private static IReadOnlyList<TorrentFileInfo> MapFiles(IList<ITorrentFile> torrentFiles, string torrentName)
    {
        var files = new List<TorrentFileInfo>(torrentFiles.Count);
        for (var index = 0; index < torrentFiles.Count; index++)
        {
            var file = torrentFiles[index];
            var relative = NormalizeRelative(Path.Combine(torrentName, file.Path));
            files.Add(new TorrentFileInfo(index, relative, file.Length));
        }

        return files;
    }

    private static string NormalizeRelative(string path) => path.Replace('\\', '/');

    private static string HashOf(InfoHashes infoHashes) => infoHashes.V1OrV2.ToHex();

    private static IPAddress? TryParseBindAddress(string? address) =>
        !string.IsNullOrWhiteSpace(address) && IPAddress.TryParse(address, out var parsed) ? parsed : null;

    // With no bind address, listen on all IPv4 + IPv6 interfaces. With one set (e.g. a VPN tun
    // address), bind ONLY that address's family so the port is not also exposed on every interface
    // via IPv6Any.
    private static Dictionary<string, IPEndPoint> BuildListenEndPoints(IPAddress? bindAddress, int port)
    {
        if (bindAddress is null)
        {
            return new Dictionary<string, IPEndPoint>
            {
                ["ipv4"] = new IPEndPoint(IPAddress.Any, port),
                ["ipv6"] = new IPEndPoint(IPAddress.IPv6Any, port),
            };
        }

        var key = bindAddress.AddressFamily == AddressFamily.InterNetworkV6 ? "ipv6" : "ipv4";
        return new Dictionary<string, IPEndPoint> { [key] = new IPEndPoint(bindAddress, port) };
    }

    private ClientEngine RequireEngine() =>
        _engine ?? throw new InvalidOperationException("Torrent engine has not started.");

    public void Dispose() => _engine?.Dispose();
}
