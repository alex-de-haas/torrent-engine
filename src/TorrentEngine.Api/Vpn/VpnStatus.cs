namespace TorrentEngine.Api.Vpn;

/// <summary>
/// Snapshot of the VPN tunnel the engine runs behind (see <c>docker/entrypoint.sh</c>).
/// <see cref="Connected"/> is the primary signal — the tunnel interface is present with an assigned
/// address. <see cref="ExitIp"/> / <see cref="ExitCountry"/> are a best-effort proof that traffic
/// actually egresses through the VPN; they require an outbound check over the tunnel and are
/// <c>null</c> when the check is disabled, still pending, or unreachable.
/// </summary>
public sealed record VpnStatus(
    bool Connected,
    string? TunnelInterface,
    string? TunnelAddress,
    string? ExitIp,
    string? ExitCountry,
    DateTimeOffset CheckedAt);
