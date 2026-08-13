using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using MonoTorrent;
using MonoTorrent.Client;
using MonoTorrent.Connections;

namespace TorrentEngine.Api.Torrents;

/// <summary>
/// MonoTorrent-backed <see cref="ITorrentEngine"/> and hosted service. Owns the <see cref="ClientEngine"/>,
/// enables PEX/LSD (and DHT unless <see cref="TorrentEngineSettings.EnableDht"/> is off) plus protocol
/// encryption, and binds the configured raw torrent port (IPv4-only unless a bind address is set — the
/// killswitch is the leak defense, but the engine also must not solicit v6). On shutdown it persists the
/// torrent roster plus fast-resume/metadata under the app data dir, and on startup restores that roster and
/// resumes the torrents, so downloads survive an engine restart.
/// <para>
/// The engine exists only while at least one torrent is registered: it is constructed on the first add and
/// disposed after the last removal, because an idle <see cref="ClientEngine"/> costs ~2.6% of a CPU core
/// doing nothing. Everything that does not need it — <see cref="Inspect"/> and the read-only views — keeps
/// working while it is absent.
/// </para>
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

    // Serializes every construction and teardown of the engine, so a concurrent add and remove can neither
    // leak a second instance nor dispose one the other is still using. _engineUsers counts the operations
    // currently holding the engine (guarded by _lifecycle): teardown waits until it drops to zero, which is
    // what makes "dispose when the roster empties" safe while an add is in flight.
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private int _engineUsers;

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
        foreach (var root in _settings.DownloadsRoots.Values)
        {
            Directory.CreateDirectory(root);
        }

        // Restore the torrent roster persisted on the previous shutdown so downloads survive an engine-only
        // restart (StopAsync writes it via SaveStateAsync). Per-torrent fast-resume/metadata is already
        // persisted under the cache dir, so a restored torrent resumes without a full re-hash. A missing or
        // corrupt state file is non-fatal — fall back to no engine rather than failing startup.
        //
        // This is the only place the state file is read: an engine constructed later in the session
        // (AcquireEngineAsync) always starts empty, so recycling can never resurrect a stale roster.
        var restored = await RestoreEngineAsync(BuildEngineSettings());
        if (restored is not null && restored.Torrents.Count == 0)
        {
            // A state file that lists nothing must not keep an idle engine alive — that idle cost is exactly
            // what this lifecycle exists to avoid.
            restored.Dispose();
            restored = null;
        }

        _engine = restored;
        if (restored is null)
        {
            _logger.LogInformation(
                "Torrent engine ready on port {Port} (port mapping: {PortMapping}, DHT: {Dht}); no torrents restored, so no engine is running until the first add.",
                _settings.Port, _settings.EnablePortMapping, _settings.EnableDht);
            return;
        }

        var restoredCount = 0;
        foreach (var manager in restored.Torrents.ToList())
        {
            var infoHash = HashOf(manager.InfoHashes);
            _managers[infoHash] = manager;
            _addedAt.TryAdd(infoHash, DateTimeOffset.UtcNow);
            manager.TorrentStateChanged += OnTorrentStateChanged;
            // A restored torrent that is already complete will transition into Seeding on resume; mark its
            // completion as already raised so the restart doesn't re-fire a `completed` event for it.
            if (manager.Complete)
            {
                _completionRaised.TryAdd(infoHash, 0);
            }

            if (!manager.HasMetadata)
            {
                _ = WaitForMetadataAsync(infoHash, manager);
            }

            restoredCount++;
        }

        // Resume the restored torrents so an engine-only restart doesn't strand them stopped. The VPN
        // gate re-pauses them on the next tick if the tunnel is down.
        try
        {
            await restored.StartAllAsync();
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to resume one or more restored torrents.");
        }

        _logger.LogInformation(
            "Torrent engine started on port {Port} (port mapping: {PortMapping}, DHT: {Dht}); restored {Count} torrent(s).",
            _settings.Port, _settings.EnablePortMapping, _settings.EnableDht, restoredCount);
    }

    /// <summary>Whether a <see cref="ClientEngine"/> is currently constructed. Tracks the roster: false while
    /// no torrent is registered. Exposed for the lifecycle tests, which have no other way to observe it.</summary>
    internal bool IsEngineRunning => _engine is not null;

    // Per-torrent settings for an add. DHT is per-torrent as well as engine-wide, so TORRENT_ENABLE_DHT has
    // to be applied here too — otherwise the engine binds no DHT endpoint while each torrent still asks for
    // a lookup, which is the half-off state the setting exists to avoid.
    internal TorrentSettings BuildTorrentSettings(TorrentLimits limits) =>
        new TorrentSettingsBuilder
        {
            AllowDht = _settings.EnableDht,
            AllowPeerExchange = true,
            CreateContainingDirectory = true,
            MaximumDownloadRate = limits.MaxDownloadRate,
            MaximumUploadRate = limits.MaxUploadRate,
        }.ToSettings();

    // The engine settings, rebuilt for each construction so a recycled engine comes up on exactly the
    // configuration the process started with. The cache dir is created here rather than once at startup, so a
    // construction later in the session still works if it went missing underneath us.
    internal EngineSettings BuildEngineSettings()
    {
        var port = _settings.Port;
        var bindAddress = TryParseBindAddress(_settings.BindAddress);
        var cacheDirectory = Path.Combine(_settings.AppDataDir, "torrent-engine");
        Directory.CreateDirectory(cacheDirectory);

        return new EngineSettingsBuilder
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
            // A null endpoint is how MonoTorrent is told not to run DHT at all, so TORRENT_ENABLE_DHT=false
            // binds no DHT socket rather than leaving a DHT engine spinning with nothing to announce.
            DhtEndPoint = _settings.EnableDht ? new IPEndPoint(bindAddress ?? IPAddress.Any, port) : null,
        }.ToSettings();
    }

    /// <summary>
    /// Takes the engine for the duration of one operation, constructing it on demand when
    /// <paramref name="createIfMissing"/> is set. The returned instance is guaranteed not to be disposed
    /// before the matching <see cref="ReleaseEngineAsync"/>; callers that get a non-null engine must always
    /// pair it with that call. Returns <c>null</c> only when there is no engine and none may be created.
    /// </summary>
    private async Task<ClientEngine?> AcquireEngineAsync(bool createIfMissing)
    {
        await _lifecycle.WaitAsync();
        try
        {
            if (_engine is null && createIfMissing)
            {
                _engine = new ClientEngine(BuildEngineSettings());
                _logger.LogInformation(
                    "Torrent engine constructed on port {Port} for the first registered torrent.", _settings.Port);
            }

            if (_engine is not null)
            {
                _engineUsers++;
            }

            return _engine;
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <summary>Ends a use taken by <see cref="AcquireEngineAsync"/>, disposing the engine once the last
    /// torrent is gone and no other operation still holds it.</summary>
    private async Task ReleaseEngineAsync()
    {
        await _lifecycle.WaitAsync();
        try
        {
            _engineUsers--;
            if (_engineUsers > 0 || !_managers.IsEmpty || _engine is null)
            {
                return;
            }

            var engine = _engine;
            try
            {
                // Persist the now-empty roster before dropping the engine: StartAsync restores from this file,
                // so leaving the previous one behind would resurrect the torrents that were just removed.
                await engine.SaveStateAsync(StateFilePath);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Could not persist the empty roster while tearing down the idle engine.");
            }

            engine.Dispose();
            _engine = null;
            _logger.LogInformation("Last torrent removed; torrent engine disposed until the next add.");
        }
        finally
        {
            _lifecycle.Release();
        }
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
        // Not passing the shutdown token: an already-cancelled one must not stop us from persisting the
        // roster. Taking the lifecycle lock keeps this off an engine another operation is tearing down.
        await _lifecycle.WaitAsync(CancellationToken.None);
        try
        {
            if (_engine is null)
            {
                // Idle with no engine: the roster was already persisted as empty when the last torrent went.
                return;
            }

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
        finally
        {
            _lifecycle.Release();
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
        Directory.CreateDirectory(saveDirectory);

        var torrentSettings = BuildTorrentSettings(limits);

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

        // Reserve the hash before touching the engine. TryAdd first (atomic), *then* the _managers check —
        // ordering matters: a torrent that finished registering on another thread populates _managers before
        // it releases its reservation, so checking _managers only after we hold the reservation closes the
        // window where both threads could proceed into MonoTorrent's unhandled "already registered" (500).
        if (!_registering.TryAdd(infoHash, 0))
        {
            throw new DuplicateTorrentException(infoHash);
        }

        try
        {
            if (_managers.ContainsKey(infoHash))
            {
                throw new DuplicateTorrentException(infoHash);
            }

            // This is the add that brings the engine up when the roster is empty. Holding the use until the
            // finally below means a concurrent remove of the last torrent cannot dispose it mid-add; if this
            // add then fails, releasing it tears the engine down again rather than leaving it idle.
            var engine = await AcquireEngineAsync(createIfMissing: true)
                ?? throw new InvalidOperationException("Torrent engine could not be created.");
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
                    // Map (and validate) the file list *before* mutating the engine: SafeRelative can reject a
                    // hostile torrent name/path, and it must not leave the torrent registered-but-unreachable.
                    var files = MapFiles(torrentFile!.Files, torrentFile.Name);
                    manager = await engine.AddAsync(torrentFile, saveDirectory, torrentSettings);
                    descriptor = new TorrentDescriptor(infoHash, torrentFile.Name, torrentFile.Size, HasMetadata: true, files);
                }

                // Record the add time before exposing the manager, so any snapshot that observes the torrent
                // also observes its AddedAt. TryAdd (not indexer assignment) so a value a racing snapshot
                // already stabilized via AddedAtOf's GetOrAdd is not clobbered with a later timestamp.
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
                await ReleaseEngineAsync();
            }
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
        if (!_managers.TryGetValue(infoHash, out var manager))
        {
            DeleteResumeData(infoHash);
            _completionRaised.TryRemove(infoHash, out _);
            _addedAt.TryRemove(infoHash, out _);
            return;
        }

        // Never create an engine here: a registered torrent implies a running one, and a remove that
        // constructed an engine only to tear it down again would be absurd. A null engine (the roster and the
        // engine disagreeing, which the lifecycle lock is there to prevent) still cleans up the local state.
        var engine = await AcquireEngineAsync(createIfMissing: false);
        try
        {
            if (engine is not null)
            {
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
            }

            // Drop the torrent from the roster before releasing the engine, so the release below sees the
            // roster this removal leaves behind and tears the engine down when it was the last one.
            _managers.TryRemove(infoHash, out _);
            manager.TorrentStateChanged -= OnTorrentStateChanged;
            _completionRaised.TryRemove(infoHash, out _);
            _addedAt.TryRemove(infoHash, out _);

            DeleteResumeData(infoHash);
        }
        finally
        {
            if (engine is not null)
            {
                await ReleaseEngineAsync();
            }
        }
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

    public int TorrentCount => _managers.Count;

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
    // root. The rooted check is platform-independent (not Path.IsPathRooted, which wouldn't see a Windows
    // drive path on Linux) so it protects cross-platform consumers regardless of where this app runs.
    // On-disk placement inside this app is separately guarded by MonoTorrent's own PathValidator.
    private static string SafeRelative(string path)
    {
        var normalized = path.Replace('\\', '/');
        var rooted = normalized.StartsWith('/')
            || (normalized.Length >= 2 && char.IsAsciiLetter(normalized[0]) && normalized[1] == ':'); // e.g. C:/…
        if (rooted || normalized.Split('/').Any(static segment => segment == ".."))
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

    public void Dispose()
    {
        _engine?.Dispose();
        _lifecycle.Dispose();
    }
}
