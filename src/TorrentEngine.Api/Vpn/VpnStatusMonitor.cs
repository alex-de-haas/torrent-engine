using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using TorrentEngine.Api.Torrents;

namespace TorrentEngine.Api.Vpn;

/// <summary>
/// Tracks the VPN tunnel the engine runs behind. The tunnel interface is read locally and cheaply
/// (no network call); the public exit IP is a best-effort outbound check over the tunnel, refreshed
/// on a long interval and cached. Runs as a background loop and raises <see cref="StatusChanged"/>
/// whenever connectivity, the tunnel address, or the exit IP changes so the SSE stream can push it.
/// </summary>
public sealed class VpnStatusMonitor(
    TorrentEngineSettings settings,
    IHttpClientFactory httpClientFactory,
    ILogger<VpnStatusMonitor> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ExitRefreshInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ExitCheckTimeout = TimeSpan.FromSeconds(8);

    // Written only by the background loop; read (as a single immutable reference) by GetStatus and the
    // change check. volatile makes the latest publish visible to the HTTP request threads.
    private volatile VpnStatus? _current;
    private DateTimeOffset _exitCheckedAt; // loop thread only

    /// <summary>Latest computed status, or <c>null</c> before the first check completes.</summary>
    public VpnStatus? Current => _current;

    /// <summary>Raised on the background loop when the reported status meaningfully changes.</summary>
    public event EventHandler<VpnStatus>? StatusChanged;

    /// <summary>
    /// Live status: re-reads the tunnel interface (cheap) and combines it with the cached exit IP.
    /// Suitable for a <c>GET /vpn</c> seed without waiting on the poll loop.
    /// </summary>
    public VpnStatus GetStatus()
    {
        var cached = _current;
        var (connected, iface, address) = ReadTunnel();
        // Only the tunnel read is live here; the exit IP is reused from the last poll, so report that
        // poll's timestamp rather than now — CheckedAt must not imply the exit IP was just re-verified.
        return new VpnStatus(
            connected,
            iface,
            address,
            connected ? cached?.ExitIp : null,
            connected ? cached?.ExitCountry : null,
            cached?.CheckedAt ?? DateTimeOffset.UtcNow);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await RefreshAsync(stoppingToken);

            using var timer = new PeriodicTimer(PollInterval);
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await RefreshAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        try
        {
            var previous = _current;
            var (connected, iface, address) = ReadTunnel();

            var exitIp = connected ? previous?.ExitIp : null;
            var exitCountry = connected ? previous?.ExitCountry : null;

            if (connected && settings.VpnExitCheckEnabled)
            {
                var justConnected = previous is not { Connected: true };
                var stale = DateTimeOffset.UtcNow - _exitCheckedAt > ExitRefreshInterval;
                // No `exitIp is null` retry: a failed check also stamps _exitCheckedAt, so a failure backs
                // off for the full ExitRefreshInterval instead of hammering the IP service every poll tick.
                if (justConnected || stale)
                {
                    (exitIp, exitCountry) = await FetchExitAsync(cancellationToken);
                    _exitCheckedAt = DateTimeOffset.UtcNow;
                }
            }

            var status = new VpnStatus(connected, iface, address, exitIp, exitCountry, DateTimeOffset.UtcNow);
            _current = status;

            if (HasChanged(previous, status))
            {
                StatusChanged?.Invoke(this, status);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A transient read/fetch error must not kill the monitor loop. Let cancellation (shutdown)
            // propagate to ExecuteAsync instead of logging it as a failure.
            logger.LogWarning(exception, "Failed to refresh VPN status.");
        }
    }

    /// <summary>Reads the tunnel interface by name; connected means it exists with an IPv4 address
    /// assigned (tun devices often report <see cref="OperationalStatus.Unknown"/>, so an assigned
    /// address is the reliable signal that the tunnel is actually up).</summary>
    private (bool Connected, string? Interface, string? Address) ReadTunnel()
    {
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (!string.Equals(ni.Name, settings.VpnInterface, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var address = ni.GetIPProperties().UnicastAddresses
                    .Select(a => a.Address)
                    .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);

                return (address is not null && ni.OperationalStatus != OperationalStatus.Down,
                    ni.Name,
                    address?.ToString());
            }
        }
        catch (NetworkInformationException exception)
        {
            logger.LogDebug(exception, "Could not enumerate network interfaces for VPN status.");
        }

        return (false, null, null);
    }

    /// <summary>Best-effort public exit IP (and country when the endpoint reports one). Goes out over
    /// the tunnel; under the killswitch it is blocked when the tunnel is down, so failure here is
    /// expected and non-fatal — the tunnel signal already covers connectivity.</summary>
    private async Task<(string? Ip, string? Country)> FetchExitAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(ExitCheckTimeout);

            var client = httpClientFactory.CreateClient();
            using var response = await client.GetAsync(settings.VpnExitCheckUrl, cts.Token);
            response.EnsureSuccessStatusCode();

            var body = (await response.Content.ReadAsStringAsync(cts.Token)).Trim();

            // Prefer a JSON body like ipinfo.io's {"ip":"...","country":"NL"}; fall back to a plain-text IP.
            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                var ip = GetString(root, "ip") ?? GetString(root, "query"); // ipinfo / ip-api shapes
                var country = GetString(root, "country") ?? GetString(root, "country_iso") ?? GetString(root, "countryCode");
                if (!string.IsNullOrWhiteSpace(ip))
                {
                    return (ip, string.IsNullOrWhiteSpace(country) ? null : country);
                }
            }
            catch (JsonException)
            {
                // Not JSON — treat the body as a bare IP.
            }

            return IPAddress.TryParse(body, out _) ? (body, null) : (null, null);
        }
        // Swallow the per-check timeout (the linked CTS firing) and transient errors, but let a genuine
        // shutdown cancellation propagate rather than masking it as a failed check.
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogDebug(exception, "VPN exit-IP check failed via {Url}.", settings.VpnExitCheckUrl);
            return (null, null);
        }
    }

    private static string? GetString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool HasChanged(VpnStatus? previous, VpnStatus current) =>
        previous is null
        || previous.Connected != current.Connected
        || previous.TunnelInterface != current.TunnelInterface
        || previous.TunnelAddress != current.TunnelAddress
        || previous.ExitIp != current.ExitIp
        || previous.ExitCountry != current.ExitCountry;
}
