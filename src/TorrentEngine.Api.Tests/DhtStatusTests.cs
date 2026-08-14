using Microsoft.Extensions.Logging.Abstractions;
using MonoTorrent.Dht;
using TorrentEngine.Api.Torrents;

namespace TorrentEngine.Api.Tests;

/// <summary>
/// DHT health reporting. The point of the feature is telling three look-alike situations apart — DHT off,
/// DHT idle because no torrent is registered, and DHT enabled but failing to come up — so these pin each
/// one, plus the lifecycle edges that start and stop DHT along with the engine.
/// </summary>
public sealed class DhtStatusTests : IDisposable
{
    private readonly string _appData =
        Path.Combine(Path.GetTempPath(), "te-dht", Guid.NewGuid().ToString("n"));

    private readonly List<MonoTorrentEngine> _engines = [];

    private MonoTorrentEngine NewEngine(bool enableDht = true)
    {
        var engine = new MonoTorrentEngine(
            new TorrentEngineSettings
            {
                AppDataDir = _appData,
                DownloadsRoots = TorrentEngineSettings.ParseDownloadsRoots(null, _appData),
                Port = Random.Shared.Next(40000, 60000),
                EnableDht = enableDht,
            },
            NullLogger<MonoTorrentEngine>.Instance);
        _engines.Add(engine);
        return engine;
    }

    private static string HashOf(int seed) => $"{seed:x40}";

    private Task AddAsync(MonoTorrentEngine engine, int seed) =>
        engine.AddAsync(
            new TorrentSource.Magnet($"magnet:?xt=urn:btih:{seed:x40}&dn=test-{seed}"),
            Path.Combine(_appData, "downloads"),
            new TorrentLimits(0, 0),
            autoStart: false,
            CancellationToken.None);

    [Fact]
    public async Task WithNoEngine_ReportsEnabledButNotRunning()
    {
        var engine = NewEngine();
        await engine.StartAsync(CancellationToken.None);

        var status = engine.GetDhtStatus();

        // Idle, not broken: DHT is configured on, there is simply no engine to run it.
        Assert.True(status.Enabled);
        Assert.False(status.Running);
        Assert.Null(status.State);
        Assert.Equal(0, status.NodeCount);
    }

    [Fact]
    public async Task WithATorrentRegistered_ReportsRunning()
    {
        var engine = NewEngine();
        await engine.StartAsync(CancellationToken.None);
        await AddAsync(engine, 1);

        var status = engine.GetDhtStatus();

        Assert.True(status.Enabled);
        Assert.True(status.Running);
        Assert.NotNull(status.State);
    }

    [Fact]
    public async Task RunningStatus_CarriesMonoTorrentsStateVerbatim()
    {
        var engine = NewEngine();
        await engine.StartAsync(CancellationToken.None);
        await AddAsync(engine, 1);

        var status = engine.GetDhtStatus();

        // The state must survive as one of MonoTorrent's own values rather than being collapsed into a
        // boolean: `Initialising` is a healthy start-up and only `NotReady` means DHT failed to come up, so
        // a consumer that cannot tell them apart would report every bootstrap as broken.
        Assert.True(
            Enum.TryParse<DhtState>(status.State, out _),
            $"expected a MonoTorrent DhtState name, got '{status.State}'");
    }

    [Fact]
    public async Task DhtDisabled_ReportsOffEvenWithATorrentRegistered()
    {
        var engine = NewEngine(enableDht: false);
        await engine.StartAsync(CancellationToken.None);
        await AddAsync(engine, 1);

        var status = engine.GetDhtStatus();

        // MonoTorrent hands out a null-object DHT reporting `NotReady` when it is disabled. Surfacing that
        // would read as "enabled but broken", which is the confusion this whole feature exists to remove.
        Assert.False(status.Enabled);
        Assert.False(status.Running);
        Assert.Null(status.State);
        Assert.Equal(0, status.NodeCount);
    }

    [Fact]
    public async Task ConstructingAndTearingDownTheEngine_RaiseStatusChanges()
    {
        var engine = NewEngine();
        await engine.StartAsync(CancellationToken.None);

        var observed = new List<DhtStatus>();
        engine.DhtStatusChanged += (_, status) => observed.Add(status);

        await AddAsync(engine, 1);
        Assert.Contains(observed, status => status.Running);

        observed.Clear();
        await engine.RemoveAsync(HashOf(1), deleteFiles: false, CancellationToken.None);

        // The teardown stops DHT as surely as a state transition would, so it has to reach subscribers too.
        Assert.Contains(observed, status => !status.Running);
    }

    [Fact]
    public async Task AfterTeardown_ReportsNotRunningAgain()
    {
        var engine = NewEngine();
        await engine.StartAsync(CancellationToken.None);
        await AddAsync(engine, 1);
        Assert.True(engine.GetDhtStatus().Running);

        await engine.RemoveAsync(HashOf(1), deleteFiles: false, CancellationToken.None);

        var status = engine.GetDhtStatus();
        Assert.False(status.Running);
        Assert.Null(status.State);
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
