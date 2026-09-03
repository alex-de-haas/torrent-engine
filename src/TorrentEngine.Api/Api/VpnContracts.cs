using TorrentEngine.Api.Vpn;

namespace TorrentEngine.Api.Api;

/// <summary>Body of <c>GET /vpn/profiles</c>: the profile the supervisor currently runs (<c>null</c> before it
/// started one, or outside the container) and every profile in the operator's folder.</summary>
public sealed record VpnProfilesResponse(string? Active, IReadOnlyList<VpnProfileInfo> Profiles);

/// <summary>Body of <c>PUT /vpn/profile</c>: the id (file name without extension) of the profile to switch to.</summary>
public sealed record SelectVpnProfileRequest(string? Id);
