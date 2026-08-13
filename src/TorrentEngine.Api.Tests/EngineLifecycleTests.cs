using Microsoft.Extensions.Logging.Abstractions;
using TorrentEngine.Api.Torrents;

namespace TorrentEngine.Api.Tests;

/// <summary>
/// The engine lifecycle: a <c>ClientEngine</c> exists only while at least one torrent is registered, so an
/// idle app pays nothing for one. These drive a real <see cref="MonoTorrentEngine"/> — magnet adds with
/// <c>autoStart: false</c>, so a torrent registers without any network activity — and pin the construction
/// and teardown edges, what keeps working while no engine exists, and that recycling never resurrects a
/// roster that was emptied.
/// </summary>
public sealed class EngineLifecycleTests : IDisposable
{
    private readonly string _appData =
        Path.Combine(Path.GetTempPath(), "te-lifecycle", Guid.NewGuid().ToString("n"));

    private readonly List<MonoTorrentEngine> _engines = [];

    private MonoTorrentEngine NewEngine(bool enableDht = true)
    {
        var engine = new MonoTorrentEngine(
            new TorrentEngineSettings
            {
                AppDataDir = _appData,
                DownloadsRoots = TorrentEngineSettings.ParseDownloadsRoots(null, _appData),
                // A high port per instance keeps a stray listener from colliding with the next test.
                Port = Random.Shared.Next(40000, 60000),
                EnableDht = enableDht,
            },
            NullLogger<MonoTorrentEngine>.Instance);
        _engines.Add(engine);
        return engine;
    }

    // A syntactically valid magnet per seed, so each add registers a distinct info hash.
    private static TorrentSource.Magnet Magnet(int seed) =>
        new($"magnet:?xt=urn:btih:{seed:x40}&dn=test-{seed}");

    private static string HashOf(int seed) => $"{seed:x40}";

    private Task<TorrentDescriptor> AddAsync(MonoTorrentEngine engine, int seed, TorrentLimits? limits = null) =>
        engine.AddAsync(Magnet(seed), Path.Combine(_appData, "downloads"), limits ?? new TorrentLimits(0, 0),
            autoStart: false, CancellationToken.None);

    private string StateFile => Path.Combine(_appData, "torrent-engine", "engine-state.bin");

    [Fact]
    public async Task Start_WithNothingToRestore_RunsNoEngine()
    {
        var engine = NewEngine();

        await engine.StartAsync(CancellationToken.None);

        Assert.False(engine.IsEngineRunning);
        Assert.Equal(0, engine.TorrentCount);
    }

    [Fact]
    public async Task FirstAdd_ConstructsEngine_AndLastRemoveDisposesIt()
    {
        var engine = NewEngine();
        await engine.StartAsync(CancellationToken.None);
        Assert.False(engine.IsEngineRunning);

        await AddAsync(engine, 1);

        Assert.True(engine.IsEngineRunning);
        Assert.Equal(1, engine.TorrentCount);

        await engine.RemoveAsync(HashOf(1), deleteFiles: false, CancellationToken.None);

        Assert.False(engine.IsEngineRunning);
        Assert.Equal(0, engine.TorrentCount);
    }

    [Fact]
    public async Task Engine_SurvivesWhileAnyTorrentRemains()
    {
        var engine = NewEngine();
        await engine.StartAsync(CancellationToken.None);
        await AddAsync(engine, 1);
        await AddAsync(engine, 2);

        await engine.RemoveAsync(HashOf(1), deleteFiles: false, CancellationToken.None);

        // One torrent left: tearing down here would strand it.
        Assert.True(engine.IsEngineRunning);
        Assert.Equal(1, engine.TorrentCount);

        await engine.RemoveAsync(HashOf(2), deleteFiles: false, CancellationToken.None);

        Assert.False(engine.IsEngineRunning);
    }

    [Fact]
    public async Task AddRemoveCycles_RebuildTheEngineWithoutError()
    {
        var engine = NewEngine();
        await engine.StartAsync(CancellationToken.None);

        // The recycle path: each add must bind cleanly on the same port a previous instance released.
        for (var cycle = 0; cycle < 3; cycle++)
        {
            await AddAsync(engine, 1);
            Assert.True(engine.IsEngineRunning);

            await engine.RemoveAsync(HashOf(1), deleteFiles: false, CancellationToken.None);
            Assert.False(engine.IsEngineRunning);
        }
    }

    [Fact]
    public async Task ConcurrentAddsAndRemoves_LeaveTheEngineAgreeingWithTheRoster()
    {
        var engine = NewEngine();
        await engine.StartAsync(CancellationToken.None);

        // Adds and removes racing each other: the invariant is that no operation ever sees a disposed
        // engine (which would surface as ObjectDisposedException) and that the engine's existence still
        // matches the roster once the dust settles.
        var adds = Enumerable.Range(1, 12).Select(seed => Task.Run(() => AddAsync(engine, seed)));
        await Task.WhenAll(adds);
        Assert.Equal(12, engine.TorrentCount);
        Assert.True(engine.IsEngineRunning);

        var churn = Enumerable.Range(1, 12).Select(seed => Task.Run(async () =>
        {
            // Manager operations run alongside the churn: MonoTorrent throws once the engine behind a manager
            // is disposed, so these racing a teardown is exactly the window the engine lease has to close.
            await engine.PauseAsync(HashOf(seed), CancellationToken.None);
            await engine.RemoveAsync(HashOf(seed), deleteFiles: false, CancellationToken.None);
            // Immediately re-adding races the teardown that the removal above may have just triggered.
            await AddAsync(engine, seed);
            await engine.StopAsync(HashOf(seed), CancellationToken.None);
            await engine.RemoveAsync(HashOf(seed), deleteFiles: false, CancellationToken.None);
        }));
        await Task.WhenAll(churn);

        Assert.Equal(0, engine.TorrentCount);
        Assert.False(engine.IsEngineRunning);
    }

    [Fact]
    public async Task ReadOnlyViews_WithNoEngine_ReportAnEmptyRoster()
    {
        var engine = NewEngine();
        await engine.StartAsync(CancellationToken.None);

        Assert.False(engine.IsEngineRunning);
        Assert.Empty(engine.GetAllSnapshots());
        Assert.Null(engine.GetSnapshot(HashOf(1)));
        Assert.Null(engine.GetFiles(HashOf(1)));
        Assert.Equal(0, engine.TorrentCount);
    }

    [Fact]
    public async Task Inspect_WithNoEngine_StillParsesTheSource()
    {
        var engine = NewEngine();
        await engine.StartAsync(CancellationToken.None);

        var descriptor = engine.Inspect(Magnet(7));

        Assert.False(engine.IsEngineRunning);
        Assert.Equal(HashOf(7), descriptor.InfoHash);
    }

    [Fact]
    public async Task RemovingAnUnknownTorrent_WithNoEngine_IsANoOp()
    {
        var engine = NewEngine();
        await engine.StartAsync(CancellationToken.None);

        await engine.RemoveAsync(HashOf(99), deleteFiles: false, CancellationToken.None);

        // A remove must never be the thing that constructs an engine.
        Assert.False(engine.IsEngineRunning);
    }

    [Fact]
    public async Task RestartAfterTheRosterWasEmptied_RestoresNothing()
    {
        var first = NewEngine();
        await first.StartAsync(CancellationToken.None);
        await AddAsync(first, 1);
        await first.RemoveAsync(HashOf(1), deleteFiles: false, CancellationToken.None);
        await first.StopAsync(CancellationToken.None);

        // The teardown persisted an empty roster, so the next process start must not bring the torrent back.
        var second = NewEngine();
        await second.StartAsync(CancellationToken.None);

        Assert.False(second.IsEngineRunning);
        Assert.Equal(0, second.TorrentCount);
    }

    [Fact]
    public async Task TeardownThatCannotPersistTheEmptyRoster_DiscardsTheStaleStateFile()
    {
        var first = NewEngine();
        await first.StartAsync(CancellationToken.None);
        await AddAsync(first, 1);
        await first.StopAsync(CancellationToken.None); // persists a roster that lists torrent 1
        Assert.True(File.Exists(StateFile));

        var second = NewEngine();
        await second.StartAsync(CancellationToken.None);
        Assert.Equal(1, second.TorrentCount);

        // Hold the state file exclusively so the teardown's rewrite fails the way a full or read-only data
        // volume would. Leaving the old file behind would resurrect a download the user deleted.
        using (new FileStream(StateFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await second.RemoveAsync(HashOf(1), deleteFiles: false, CancellationToken.None);
        }

        Assert.False(File.Exists(StateFile));

        var third = NewEngine();
        await third.StartAsync(CancellationToken.None);
        Assert.Equal(0, third.TorrentCount);
    }

    [Fact]
    public async Task ManagerOperations_WithNoEngine_AreNoOpsAndConstructNothing()
    {
        var engine = NewEngine();
        await engine.StartAsync(CancellationToken.None);

        await engine.PauseAsync(HashOf(1), CancellationToken.None);
        await engine.ResumeAsync(HashOf(1), CancellationToken.None);
        await engine.StopAsync(HashOf(1), CancellationToken.None);

        Assert.False(engine.IsEngineRunning);
    }

    [Fact]
    public async Task RestoredTorrent_PicksUpAChangedDhtSetting_WithoutLosingItsRateLimits()
    {
        var first = NewEngine(enableDht: true);
        await first.StartAsync(CancellationToken.None);
        await AddAsync(first, 1, new TorrentLimits(MaxDownloadRate: 12345, MaxUploadRate: 0));
        Assert.True(first.SettingsOf(HashOf(1))!.AllowDht);
        await first.StopAsync(CancellationToken.None);

        // The operator turns DHT off and restarts. A restored manager carries the settings serialized last
        // time, so without re-applying it the torrent would keep asking for lookups DHT can no longer serve.
        var second = NewEngine(enableDht: false);
        await second.StartAsync(CancellationToken.None);

        var restored = second.SettingsOf(HashOf(1));
        Assert.NotNull(restored);
        Assert.False(restored.AllowDht);
        // Only the DHT flag may change — the per-download limit is the caller's, not the engine's.
        Assert.Equal(12345, restored.MaximumDownloadRate);
    }

    [Fact]
    public async Task RestartWithTorrentsRegistered_RestoresTheRoster()
    {
        var first = NewEngine();
        await first.StartAsync(CancellationToken.None);
        await AddAsync(first, 1);
        await first.StopAsync(CancellationToken.None);

        var second = NewEngine();
        await second.StartAsync(CancellationToken.None);

        Assert.True(second.IsEngineRunning);
        Assert.Equal(1, second.TorrentCount);
    }

    [Fact]
    public void DhtEnabledByDefault_BindsADhtEndpointAndTorrentsAllowDht()
    {
        var engine = NewEngine();

        Assert.NotNull(engine.BuildEngineSettings().DhtEndPoint);
        Assert.True(engine.BuildTorrentSettings(new TorrentLimits(0, 0)).AllowDht);
    }

    [Fact]
    public void DhtDisabled_BindsNoDhtEndpointAndTorrentsDoNotAllowDht()
    {
        var engine = NewEngine(enableDht: false);

        // Both halves matter: a null endpoint stops the engine binding a DHT socket, and AllowDht false stops
        // each torrent asking for lookups that would have nowhere to go.
        Assert.Null(engine.BuildEngineSettings().DhtEndPoint);
        Assert.False(engine.BuildTorrentSettings(new TorrentLimits(0, 0)).AllowDht);
    }

    public void Dispose()
    {
        foreach (var engine in _engines)
        {
            engine.Dispose();
        }

        try
        {
            if (Directory.Exists(_appData))
            {
                Directory.Delete(_appData, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leftover temp dir must not fail the run.
        }
    }
}
