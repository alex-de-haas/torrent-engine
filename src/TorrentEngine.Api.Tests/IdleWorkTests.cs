using Imposter.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using TorrentEngine.Api.Realtime;
using TorrentEngine.Api.Torrents;
using TorrentEngine.Api.Vpn;

[assembly: GenerateImposter(typeof(IHttpClientFactory))]

namespace TorrentEngine.Api.Tests;

/// <summary>
/// Covers the work both background loops skip when there is nothing to act on. An idle engine ran these
/// ticks forever — the broadcaster building snapshots with no SSE subscriber attached, the VPN gate
/// enumerating every network interface with no torrent registered — so the skips are asserted here.
/// </summary>
public sealed class IdleWorkTests
{
    private static TorrentSnapshot Snapshot(string infoHash, string state) =>
        new(infoHash, "Test", state, false, 0, 0, 0, 0, 0, 100,
            Seeds: 0, Leeches: 0, AvailablePeers: 0, DownloadedBytes: 0, UploadedBytes: 0,
            RemainingBytes: 100, TotalPieces: 0, CompletePieces: 0, PieceLengthBytes: 0,
            EtaSeconds: null, AddedAt: DateTimeOffset.UnixEpoch, ElapsedSeconds: 0);

    private static VpnStatusMonitor Monitor(string vpnInterface) =>
        new(
            new TorrentEngineSettings
            {
                AppDataDir = "/tmp/te",
                DownloadsRoots = new Dictionary<string, string>(),
                VpnInterface = vpnInterface,
                VpnExitCheckEnabled = false,
            },
            IHttpClientFactory.Imposter().Instance(),
            NullLogger<VpnStatusMonitor>.Instance);

    // A name no real interface carries, so the monitor reads the tunnel as down.
    private const string MissingInterface = "tun-does-not-exist";

    [Fact]
    public void EventStream_TracksWhetherAnyoneIsSubscribed()
    {
        var stream = new TorrentEventStream();
        Assert.False(stream.HasSubscribers);

        var (id, _) = stream.Subscribe();
        Assert.True(stream.HasSubscribers);

        stream.Unsubscribe(id);
        Assert.False(stream.HasSubscribers);
    }

    [Fact]
    public void ProgressTick_WithoutSubscribers_DoesNotTouchTheEngine()
    {
        var engine = ITorrentEngine.Imposter();
        var broadcaster = new TorrentProgressBroadcaster(
            engine.Instance(), Monitor(MissingInterface), new TorrentEventStream(),
            NullLogger<TorrentProgressBroadcaster>.Instance);

        broadcaster.PublishProgressTick();

        engine.GetAllSnapshots().Called(Count.Never());
    }

    [Fact]
    public void ProgressTick_WithSubscriber_PublishesOneFramePerTorrent()
    {
        var engine = ITorrentEngine.Imposter();
        engine.GetAllSnapshots().Returns(new[] { Snapshot("abc123", "Downloading") });

        var stream = new TorrentEventStream();
        var broadcaster = new TorrentProgressBroadcaster(
            engine.Instance(), Monitor(MissingInterface), stream,
            NullLogger<TorrentProgressBroadcaster>.Instance);
        var (_, reader) = stream.Subscribe();

        broadcaster.PublishProgressTick();

        Assert.True(reader.TryRead(out var evt));
        Assert.Equal("progress", evt!.Type);
        Assert.Equal("abc123", evt.InfoHash);
        Assert.False(reader.TryRead(out _));
    }

    [Fact]
    public async Task Reconcile_WithNothingRegistered_DoesNotTouchTheEngine()
    {
        var engine = ITorrentEngine.Imposter();
        engine.TorrentCount.Getter().Returns(0);

        // Tunnel reads as down, so without the idle skip this pass would enumerate the torrents to pause.
        var gate = new VpnDownloadGate(engine.Instance(), Monitor(MissingInterface), NullLogger<VpnDownloadGate>.Instance);

        await gate.ReconcileAsync(CancellationToken.None);

        engine.GetAllSnapshots().Called(Count.Never());
    }

    [Fact]
    public async Task Reconcile_WithTunnelDown_PausesActiveTorrents()
    {
        var engine = ITorrentEngine.Imposter();
        engine.TorrentCount.Getter().Returns(1);
        engine.GetAllSnapshots().Returns(new[] { Snapshot("abc123", "Downloading") });
        engine.PauseAsync(Arg<string>.Any(), Arg<CancellationToken>.Any()).Returns(Task.CompletedTask);

        var gate = new VpnDownloadGate(engine.Instance(), Monitor(MissingInterface), NullLogger<VpnDownloadGate>.Instance);

        await gate.ReconcileAsync(CancellationToken.None);

        engine.PauseAsync(Arg<string>.Is("abc123"), Arg<CancellationToken>.Any()).Called(Count.Once());
    }

    [Fact]
    public async Task Reconcile_WithNothingRegisteredButAGatedPause_StillRuns()
    {
        // A torrent removed mid-outage leaves the gate holding its hash. The idle skip must not fire while
        // the gate still owns one, or that entry would never be reconciled away.
        var registered = 1;
        var engine = ITorrentEngine.Imposter();
        engine.TorrentCount.Getter().Returns(() => registered);
        engine.GetAllSnapshots().Returns(new[] { Snapshot("abc123", "Downloading") });
        engine.PauseAsync(Arg<string>.Any(), Arg<CancellationToken>.Any()).Returns(Task.CompletedTask);

        var gate = new VpnDownloadGate(engine.Instance(), Monitor(MissingInterface), NullLogger<VpnDownloadGate>.Instance);
        await gate.ReconcileAsync(CancellationToken.None); // pauses abc123 and records it

        registered = 0; // the torrent is removed while the tunnel is still down
        await gate.ReconcileAsync(CancellationToken.None);

        engine.GetAllSnapshots().Called(Count.Twice());
    }
}
