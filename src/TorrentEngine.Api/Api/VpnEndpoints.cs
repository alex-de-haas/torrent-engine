using TorrentEngine.Api.Vpn;

namespace TorrentEngine.Api.Api;

/// <summary>Maps the VPN surface: tunnel status, the operator's profile list, and profile selection.</summary>
public static class VpnEndpoints
{
    public static void MapVpnEndpoints(this IEndpointRouteBuilder app)
    {
        // Current VPN tunnel status (a consumer seeds this on connect, then receives `vpn` SSE events).
        app.MapGet("/vpn", (VpnStatusMonitor vpn) => Results.Ok(vpn.GetStatus()));

        // The profiles folder, listed live so a file dropped in shows up without a restart. `active` is what the
        // entrypoint's supervisor currently runs — null before it started one, or outside the container.
        app.MapGet("/vpn/profiles", (IVpnProfileCatalog catalog) =>
            Results.Ok(new VpnProfilesResponse(catalog.ReadSupervisorStatus().Profile, catalog.ListProfiles())));

        // Records the wish only: the id lands in the app-data selection file and the supervisor (root, NET_ADMIN)
        // performs the switch — re-keys the killswitch, restarts OpenVPN — and reports back through /vpn and the
        // `vpn` SSE event (pendingProfile while in flight, lastError if it failed). Hence 202, not 200.
        app.MapPut("/vpn/profile", (SelectVpnProfileRequest request, IVpnProfileCatalog catalog, VpnStatusMonitor vpn) =>
        {
            var id = request.Id?.Trim();
            switch (catalog.Lookup(id))
            {
                case VpnProfileLookup.Malformed:
                    return Results.BadRequest(new ErrorResponse(
                        "id must be a profile's file name without its extension: no path separators, no leading dot."));
                case VpnProfileLookup.NotConfigured:
                    return Results.NotFound(new ErrorResponse(
                        "No VPN profiles folder is configured: bind the app's 'vpn' external mount."));
                case VpnProfileLookup.NotFound:
                {
                    var known = string.Join(", ", catalog.ListProfiles().Select(profile => profile.Id));
                    return Results.NotFound(new ErrorResponse(
                        $"No VPN profile '{id}' (configured: {(known.Length == 0 ? "none" : known)})."));
                }
            }

            catalog.Select(id!);
            return Results.Accepted(value: vpn.GetStatus());
        });
    }
}
