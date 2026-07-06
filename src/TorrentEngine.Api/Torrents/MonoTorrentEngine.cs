using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using MonoTorrent;
using MonoTorrent.Client;
using MonoTorrent.Connections;

namespace TorrentEngine.Api.Torrents;

/// <summary>
/// MonoTorrent-backed <see cref="ITorrentEngine"/> and hosted service. Owns the <see cref="ClientEngine"/>,
/// enables DHT/PEX/LSD and protocol encryption, and binds the configured raw torrent port (IPv4-only unless
/// a bind address is set — the killswitch is the leak defense, but the engine also must not solicit v6). On
/// shutdown it persists the torrent roster plus fast-resume/metadata under the app data dir, and on startup
/// restores that roster and resumes the torrents, so downloads survive an engine restart.
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
    // Info hashes with an AddAsync in flight. Reserved before the (awaited) engine add so two concurrent
    // adds of the same hash can't both pass the endpoint's snapshot pre-check and race MonoTorrent into an
    // unhandled "already registered" throw — the loser gets a DuplicateTorrentException (→ 409) instead.
    private readonly ConcurrentDictionary<string, byte> _registering = new(StringComparer.OrdinalIgnoreCase);

    private ClientEngine? _engine;

    public MonoTorrentEngine(TorrentEngineSettings settings, ILogger<MonoTorrentEngine> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    public event EventHandler<string>? MetadataReceived;
    public event EventHandler<string>? DownloadCompleted;
    public event EventHandler<string>? DownloadErrored;

    public async Task StartAsync(CancellationToken cancellationToken)
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
        var settings = builder.ToSettings();

        // Restore the torrent roster persisted on the previous shutdown so downloads survive an engine-only
        // restart (StopAsync writes it via SaveStateAsync). Per-torrent fast-resume/metadata is already
        // persisted under the cache dir, so a restored torrent resumes without a full re-hash. A missing or
        // corrupt state file is non-fatal — fall back to a fresh engine rather than failing startup.
        var restored = await RestoreEngineAsync(settings);
        _engine = restored ?? new ClientEngine(settings);

        var restoredCount = 0;
        foreach (var manager in _engine.Torrents.ToList())
        {
            var infoHash = HashOf(manager.InfoHashes);
            _managers[infoHash] = manager;
            _addedAt.TryAdd(infoHash, DateTimeOffset.UtcNow);
            manager.TorrentStateChanged += OnTorrentStateChanged;
            if (!manager.HasMetadata)
            {
                _ = WaitForMetadataAsync(infoHash, manager);
            }

            restoredCount++;
        }

        if (restoredCount > 0)
        {
            // Resume the restored torrents so an engine-only restart doesn't strand them stopped. The VPN
            // gate re-pauses them on the next tick if the tunnel is down.
            try
            {
                await _engine.StartAllAsync();
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Failed to resume one or more restored torrents.");
            }
        }

        _logger.LogInformation(
            "Torrent engine started on port {Port} (port mapping: {PortMapping}); restored {Count} torrent(s).",
            port, _settings.EnablePortMapping, restoredCount);
    }

    // Rebuilds the engine from the persisted state file when present, re-applying the freshly-computed
    // settings over the saved ones (so a changed port/rate/cache dir still takes effect). Returns null when
    // there is no state to restore or it can't be read, so the caller starts a fresh engine.
    private async Task<ClientEngine?> RestoreEngineAsync(EngineSettings settings)
    {
        var stateFile = StateFilePath;
        if (!File.Exists(stateFile))
        {
            return null;
        }

        try
        {
            var engine = await ClientEngine.RestoreStateAsync(stateFile);
            try
            {
                await engine.UpdateSettingsAsync(settings);
            }
            catch (Exception exception)
            {
                // Applying current settings failed — keep the restored engine (and its roster) on the saved
                // settings rather than throwing the downloads away for a config-drift edge case.
                _logger.LogWarning(exception, "Restored engine kept its persisted settings; could not apply the current ones.");
            }

            return engine;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Could not restore engine state from {StateFile}; starting fresh.", stateFile);
            return null;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_engine is not null)
        {
            try
            {
                await _engine.StopAllAsync(TimeSpan.FromSeconds(10));
                // Persist the roster to a file (the parameterless overload only returns the bytes and drops
                // them) so StartAsync can restore it. Best-effort: a write failure must not block shutdown.
                await _engine.SaveStateAsync(StateFilePath);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Error while stopping the torrent engine.");
            }
        }
    }

    /// <summary>Path of the persisted engine roster (the torrent list + settings) written on shutdown and
    /// restored on startup. Lives alongside the fast-resume/metadata cache under the app data dir.</summary>
    private string StateFilePath => Path.Combine(_settings.AppDataDir, "torrent-engine", "engine-state.bin");

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

        // Parse the source (and read its info hash) before touching the engine, so we can atomically reserve
        // the hash and reject a concurrent duplicate add rather than let MonoTorrent throw "already registered".
        MagnetLink? magnetLink = null;
        Torrent? torrentFile = null;
        string infoHash;
        switch (source)
        {
            case TorrentSource.Magnet magnet:
                if (!MagnetLink.TryParse(magnet.Uri, out magnetLink))
                {
                    throw new ArgumentException("Invalid magnet link.", nameof(source));
                }

                infoHash = HashOf(magnetLink.InfoHashes);
                break;

            case TorrentSource.File file:
                torrentFile = await Torrent.LoadAsync(file.Content.AsMemory());
                infoHash = HashOf(torrentFile.InfoHashes);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(source));
        }

        // Reserve the hash: fail if it is already registered or another add for it is in flight. TryAdd makes
        // the reservation atomic, so two concurrent adds that both cleared the endpoint's snapshot pre-check
        // can't both proceed into the engine.
        if (_managers.ContainsKey(infoHash) || !_registering.TryAdd(infoHash, 0))
        {
            throw new DuplicateTorrentException(infoHash);
        }

        try
        {
            TorrentManager manager;
            TorrentDescriptor descriptor;
            if (magnetLink is not null)
            {
                manager = await engine.AddAsync(magnetLink, saveDirectory, torrentSettings);
                descriptor = new TorrentDescriptor(infoHash, magnetLink.Name, magnetLink.Size, HasMetadata: false, []);
            }
            else
            {
                manager = await engine.AddAsync(torrentFile!, saveDirectory, torrentSettings);
                descriptor = new TorrentDescriptor(
                    infoHash, torrentFile!.Name, torrentFile.Size, HasMetadata: true, MapFiles(torrentFile.Files, torrentFile.Name));
            }

            // Record the add time before exposing the manager, so any snapshot that observes the torrent also
            // observes its AddedAt. TryAdd (not indexer assignment) so a value a racing snapshot already
            // stabilized via AddedAtOf's GetOrAdd is not clobbered with a later timestamp.
            _addedAt.TryAdd(infoHash, DateTimeOffset.UtcNow);
            _managers[infoHash] = manager;
            manager.TorrentStateChanged += OnTorrentStateChanged;

            if (autoStart)
            {
                await manager.StartAsync();
            }

            if (!descriptor.HasMetadata)
            {
                _ = WaitForMetadataAsync(infoHash, manager);
            }
            else
            {
                RaiseMetadata(infoHash);
            }

            return descriptor;
        }
        finally
        {
            // The manager is in _managers now (or the add threw); either way the in-flight reservation is done.
            _registering.TryRemove(infoHash, out _);
        }
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
            var relative = SafeRelative(Path.GetRelativePath(manager.SavePath, file.FullPath));
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
            var relative = SafeRelative(Path.Combine(torrentName, file.Path));
            files.Add(new TorrentFileInfo(index, relative, file.Length));
        }

        return files;
    }

    // Normalizes to POSIX separators and rejects any path that is rooted or walks up via a ".." segment.
    // The torrent name (and, for a descriptor, the file paths) is attacker-controlled, so an emitted
    // RelativePath must never lexically escape the save directory when a consumer combines it with its own
    // root. On-disk placement inside this app is separately guarded by MonoTorrent's own PathValidator.
    private static string SafeRelative(string path)
    {
        var normalized = path.Replace('\\', '/');
        if (Path.IsPathRooted(normalized) || normalized.Split('/').Any(static segment => segment == ".."))
        {
            throw new ArgumentException($"Torrent file path '{path}' is not a safe relative path.");
        }

        return normalized;
    }

    private static string HashOf(InfoHashes infoHashes) => infoHashes.V1OrV2.ToHex();

    private static IPAddress? TryParseBindAddress(string? address) =>
        !string.IsNullOrWhiteSpace(address) && IPAddress.TryParse(address, out var parsed) ? parsed : null;

    // With no bind address, listen on IPv4 only. The killswitch (docker/entrypoint.sh) confines egress to
    // the tunnel with iptables *and* ip6tables, but the engine must not solicit IPv6 peers/DHT in the first
    // place: binding IPv6Any here would advertise and accept v6 traffic that, on an IPv6-enabled docker
    // network, could bypass the (historically IPv4-only) tunnel. Set TORRENT_BIND_ADDRESS to a specific
    // address (e.g. the tun interface) to bind only that address's family.
    private static Dictionary<string, IPEndPoint> BuildListenEndPoints(IPAddress? bindAddress, int port)
    {
        if (bindAddress is null)
        {
            return new Dictionary<string, IPEndPoint> { ["ipv4"] = new IPEndPoint(IPAddress.Any, port) };
        }

        var key = bindAddress.AddressFamily == AddressFamily.InterNetworkV6 ? "ipv6" : "ipv4";
        return new Dictionary<string, IPEndPoint> { [key] = new IPEndPoint(bindAddress, port) };
    }

    private ClientEngine RequireEngine() =>
        _engine ?? throw new InvalidOperationException("Torrent engine has not started.");

    public void Dispose() => _engine?.Dispose();
}
