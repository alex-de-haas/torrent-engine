using TorrentEngine.Api.Vpn;

namespace TorrentEngine.Api.Tests;

/// <summary>The monitor's decision rules: what earns an SSE <c>vpn</c> event, when the exit IP is re-verified, and
/// when a cached exit still describes the tunnel we are on.</summary>
public sealed class VpnStatusMonitorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 3, 10, 0, 0, TimeSpan.Zero);

    private static VpnStatus Status(
        bool connected = true, string? exitIp = "1.2.3.4", string? profile = "nl-ams",
        string? pending = null, string? error = null, DateTimeOffset? checkedAt = null) =>
        new(connected, "tun0", "10.8.0.2", exitIp, "NL", checkedAt ?? T0, profile, pending, error);

    [Fact]
    public void HasChanged_NoPrevious_IsAChange()
    {
        Assert.True(VpnStatusMonitor.HasChanged(null, Status()));
    }

    [Fact]
    public void HasChanged_OnlyCheckedAtDiffers_IsNotAChange()
    {
        Assert.False(VpnStatusMonitor.HasChanged(Status(), Status(checkedAt: T0.AddMinutes(5))));
    }

    [Fact]
    public void HasChanged_TunnelOrExitDiffers_IsAChange()
    {
        Assert.True(VpnStatusMonitor.HasChanged(Status(), Status(connected: false)));
        Assert.True(VpnStatusMonitor.HasChanged(Status(), Status(exitIp: "5.6.7.8")));
    }

    [Fact]
    public void HasChanged_SupervisorTrioDiffers_IsAChange()
    {
        Assert.True(VpnStatusMonitor.HasChanged(Status(), Status(profile: "de-fra")));
        Assert.True(VpnStatusMonitor.HasChanged(Status(), Status(pending: "de-fra")));
        Assert.True(VpnStatusMonitor.HasChanged(Status(), Status(error: "openvpn exited")));
        // …and the trio going back to normal is a change too (the picker clears its "switching" state on it).
        Assert.True(VpnStatusMonitor.HasChanged(Status(pending: "de-fra"), Status()));
    }

    [Fact]
    public void NeedsExitCheck_OnConnect_OrWhenStale()
    {
        Assert.True(VpnStatusMonitor.NeedsExitCheck(null, "10.8.0.2", "nl-ams", stale: false));
        Assert.True(VpnStatusMonitor.NeedsExitCheck(Status(connected: false), "10.8.0.2", "nl-ams", stale: false));
        Assert.True(VpnStatusMonitor.NeedsExitCheck(Status(), "10.8.0.2", "nl-ams", stale: true));
        Assert.False(VpnStatusMonitor.NeedsExitCheck(Status(), "10.8.0.2", "nl-ams", stale: false));
    }

    [Fact]
    public void NeedsExitCheck_WhenTheTunnelIsADifferentOne()
    {
        // A profile switch that completes between two polls keeps Connected true on both sides; the cached exit
        // then belongs to the previous server and must be re-verified, not carried over for five minutes.
        Assert.True(VpnStatusMonitor.NeedsExitCheck(Status(), "10.8.0.2", "de-fra", stale: false));
        Assert.True(VpnStatusMonitor.NeedsExitCheck(Status(), "10.9.0.2", "nl-ams", stale: false));
    }

    [Fact]
    public void ExitStillApplies_OnlyWhileOnTheSameTunnel()
    {
        Assert.True(VpnStatusMonitor.ExitStillApplies(Status(), connected: true, "10.8.0.2", "nl-ams"));
        Assert.False(VpnStatusMonitor.ExitStillApplies(null, connected: true, "10.8.0.2", "nl-ams"));
        Assert.False(VpnStatusMonitor.ExitStillApplies(Status(), connected: false, "10.8.0.2", "nl-ams"));
        Assert.False(VpnStatusMonitor.ExitStillApplies(Status(), connected: true, "10.8.0.2", "de-fra"));
        Assert.False(VpnStatusMonitor.ExitStillApplies(Status(), connected: true, "10.9.0.2", "nl-ams"));
    }
}
