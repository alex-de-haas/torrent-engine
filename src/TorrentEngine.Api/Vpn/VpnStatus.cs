namespace TorrentEngine.Api.Vpn;

/// <summary>
/// Snapshot of the VPN tunnel the engine runs behind (see <c>docker/entrypoint.sh</c>).
/// <see cref="Connected"/> is the primary signal — the tunnel interface is present with an assigned
/// address. <see cref="ExitIp"/> / <see cref="ExitCountry"/> are a best-effort proof that traffic
/// actually egresses through the VPN; they require an outbound check over the tunnel and are
/// <c>null</c> when the check is disabled, still pending, or unreachable.
/// <see cref="Profile"/> / <see cref="PendingProfile"/> / <see cref="LastError"/> come from the
/// entrypoint's supervisor: the OpenVPN profile it runs, the one it is switching to, and why its last
/// start or switch failed. All three are <c>null</c> outside the container, where no supervisor runs.
/// </summary>
public sealed record VpnStatus(
    bool Connected,
    string? TunnelInterface,
    string? TunnelAddress,
    string? ExitIp,
    string? ExitCountry,
    DateTimeOffset CheckedAt,
    string? Profile = null,
    string? PendingProfile = null,
    string? LastError = null);
