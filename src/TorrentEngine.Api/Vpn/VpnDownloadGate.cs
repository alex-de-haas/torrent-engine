using TorrentEngine.Api.Torrents;

namespace TorrentEngine.Api.Vpn;

/// <summary>
/// Stops downloads while the VPN tunnel is down. The killswitch already blocks the traffic, so this is
/// about not churning on connections that can't leave and surfacing a clean paused state instead of a
/// silently-stalled "downloading at 0 B/s": when the tunnel drops, active torrents are paused; when it
/// recovers, the ones this gate paused are resumed. It reconciles on a short tick (rather than only on
/// the status-change event) so torrents added during an outage are paused too, and so a user resume that
/// can't succeed under the killswitch doesn't leave a torrent spinning.
/// </summary>
public sealed class VpnDownloadGate(
    ITorrentEngine engine,
    VpnStatusMonitor monitor,
    ILogger<VpnDownloadGate> logger) : BackgroundService
{
    private static readonly TimeSpan ReconcileInterval = TimeSpan.FromSeconds(5);

    // Info hashes this gate paused, so recovery resumes only those — never a torrent the user paused.
    private readonly HashSet<string> _gatedPaused = new(StringComparer.OrdinalIgnoreCase);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var timer = new PeriodicTimer(ReconcileInterval);
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await ReconcileAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    private async Task ReconcileAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!monitor.GetStatus().Connected)
            {
                // Tunnel down: pause anything still transferring (incl. torrents added during the outage).
                foreach (var snapshot in engine.GetAllSnapshots())
                {
                    if (IsActive(snapshot.EngineState) && _gatedPaused.Add(snapshot.InfoHash))
                    {
                        await engine.PauseAsync(snapshot.InfoHash, cancellationToken);
                        logger.LogInformation("VPN tunnel down — paused {InfoHash}.", snapshot.InfoHash);
                    }
                }
            }
            else if (_gatedPaused.Count > 0)
            {
                // Tunnel restored: resume exactly what this gate paused (a removed torrent resumes to a no-op).
                foreach (var infoHash in _gatedPaused.ToList())
                {
                    await engine.ResumeAsync(infoHash, cancellationToken);
                    logger.LogInformation("VPN tunnel restored — resumed {InfoHash}.", infoHash);
                }

                _gatedPaused.Clear();
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "VPN download gate reconcile failed.");
        }
    }

    // States that involve (killswitch-blocked) network activity worth pausing. Already-paused/stopped/
    // errored torrents are left as they are.
    private static bool IsActive(string state) =>
        state is not ("Paused" or "Stopped" or "Stopping" or "Error");
}
