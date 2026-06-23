using TorrentEngine.Api.Torrents;
using TorrentEngine.Api.Vpn;

namespace TorrentEngine.Api.Realtime;

/// <summary>
/// Bridges engine events and a periodic progress tick onto the <see cref="TorrentEventStream"/>:
/// live progress for every active torrent every 1.5s, plus metadata/completed/errored transitions
/// and VPN tunnel status changes.
/// </summary>
public sealed class TorrentProgressBroadcaster(
    ITorrentEngine engine,
    VpnStatusMonitor vpn,
    TorrentEventStream stream,
    ILogger<TorrentProgressBroadcaster> logger) : BackgroundService
{
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(1500);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        engine.MetadataReceived += OnMetadataReceived;
        engine.DownloadCompleted += OnDownloadCompleted;
        engine.DownloadErrored += OnDownloadErrored;
        vpn.StatusChanged += OnVpnStatusChanged;

        try
        {
            using var timer = new PeriodicTimer(ProgressInterval);
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    foreach (var snapshot in engine.GetAllSnapshots())
                    {
                        stream.Publish(new TorrentEvent("progress", snapshot.InfoHash, snapshot));
                    }
                }
                catch (Exception exception)
                {
                    // A transient engine error must not kill the broadcast loop forever.
                    logger.LogError(exception, "Error broadcasting periodic torrent progress.");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        finally
        {
            engine.MetadataReceived -= OnMetadataReceived;
            engine.DownloadCompleted -= OnDownloadCompleted;
            engine.DownloadErrored -= OnDownloadErrored;
            vpn.StatusChanged -= OnVpnStatusChanged;
        }
    }

    private void OnMetadataReceived(object? sender, string infoHash) => Publish("metadata-received", infoHash);

    private void OnDownloadCompleted(object? sender, string infoHash) => Publish("completed", infoHash);

    private void OnDownloadErrored(object? sender, string infoHash) => Publish("errored", infoHash);

    private void OnVpnStatusChanged(object? sender, VpnStatus status) =>
        stream.Publish(new TorrentEvent("vpn", string.Empty, null, status));

    private void Publish(string type, string infoHash)
    {
        try
        {
            stream.Publish(new TorrentEvent(type, infoHash, engine.GetSnapshot(infoHash)));
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to publish {Type} event for {InfoHash}.", type, infoHash);
        }
    }
}
