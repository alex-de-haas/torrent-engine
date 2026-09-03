using TorrentEngine.Api.Vpn;

namespace TorrentEngine.Api.Tests;

/// <summary>The monitor's change predicate — what earns an SSE <c>vpn</c> event and what does not.</summary>
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
}
