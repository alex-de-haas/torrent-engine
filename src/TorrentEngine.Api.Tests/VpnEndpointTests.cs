using System.Net;
using System.Net.Http.Json;
using Imposter.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TorrentEngine.Api.Api;
using TorrentEngine.Api.Torrents;
using TorrentEngine.Api.Vpn;

[assembly: GenerateImposter(typeof(IVpnProfileCatalog))]

namespace TorrentEngine.Api.Tests;

/// <summary>
/// The VPN surface on an in-memory TestServer hosting only <see cref="VpnEndpoints.MapVpnEndpoints"/>: the
/// profile list, and the validation and side effect of a profile selection. The read paths use an Imposter
/// catalog; the selection test uses the real catalog on a temp folder so the file the supervisor watches is
/// asserted, not mocked.
/// </summary>
public sealed class VpnEndpointTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "te-vpn-api-" + Guid.NewGuid().ToString("N"));

    public VpnEndpointTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "profiles"));
        Directory.CreateDirectory(Path.Combine(_root, "state"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private TorrentEngineSettings Settings() => new()
    {
        AppDataDir = Path.Combine(_root, "data"),
        DownloadsRoots = new Dictionary<string, string>(),
        VpnProfilesDirectory = Path.Combine(_root, "profiles"),
        VpnStateDir = Path.Combine(_root, "state"),
        VpnInterface = "tun-does-not-exist",
        VpnExitCheckEnabled = false,
    };

    private static async Task<(HttpClient Client, WebApplication App)> HostAsync(TorrentEngineSettings settings, IVpnProfileCatalog catalog)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(settings);
        builder.Services.AddSingleton(catalog);
        // Not registered as a hosted service: /vpn reads live, so the poll loop is not needed here.
        builder.Services.AddSingleton(new VpnStatusMonitor(
            settings, IHttpClientFactory.Imposter().Instance(), catalog, NullLogger<VpnStatusMonitor>.Instance));
        var app = builder.Build();
        app.MapVpnEndpoints();
        await app.StartAsync();
        return (app.GetTestClient(), app);
    }

    [Fact]
    public async Task GetProfiles_ReturnsTheActiveProfileAndTheList()
    {
        var imposter = IVpnProfileCatalog.Imposter();
        imposter.ReadSupervisorStatus().Returns(new VpnSupervisorStatus("nl-ams", null, null));
        imposter.ListProfiles().Returns(new[] { new VpnProfileInfo("de-fra", "de.example:1194"), new VpnProfileInfo("nl-ams", "nl.example:443") });

        var (client, app) = await HostAsync(Settings(), imposter.Instance());
        await using var _ = app;

        var response = await client.GetAsync("/vpn/profiles");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ProfilesBody>();
        Assert.Equal("nl-ams", body!.Active);
        Assert.Equal(["de-fra", "nl-ams"], body.Profiles.Select(p => p.Id).ToArray());
        Assert.Equal("nl.example:443", body.Profiles[1].Remote);
    }

    [Fact]
    public async Task PutProfile_MalformedId_ReturnsBadRequest()
    {
        var imposter = IVpnProfileCatalog.Imposter();
        imposter.Lookup(Arg<string?>.Any()).Returns(VpnProfileLookup.Malformed);

        var (client, app) = await HostAsync(Settings(), imposter.Instance());
        await using var _ = app;

        var response = await client.PutAsJsonAsync("/vpn/profile", new { id = "../etc" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ErrorBody>();
        Assert.Contains("file name", body!.Error);
    }

    [Fact]
    public async Task PutProfile_UnknownId_ReturnsNotFound_ListingTheKnownOnes()
    {
        var imposter = IVpnProfileCatalog.Imposter();
        imposter.Lookup(Arg<string?>.Any()).Returns(VpnProfileLookup.NotFound);
        imposter.ListProfiles().Returns(new[] { new VpnProfileInfo("nl-ams", null) });

        var (client, app) = await HostAsync(Settings(), imposter.Instance());
        await using var _ = app;

        var response = await client.PutAsJsonAsync("/vpn/profile", new { id = "zz-top" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ErrorBody>();
        Assert.Contains("zz-top", body!.Error);
        Assert.Contains("nl-ams", body.Error);
    }

    [Fact]
    public async Task PutProfile_Unconfigured_ReturnsNotFound_PointingAtTheMount()
    {
        var imposter = IVpnProfileCatalog.Imposter();
        imposter.Lookup(Arg<string?>.Any()).Returns(VpnProfileLookup.NotConfigured);

        var (client, app) = await HostAsync(Settings(), imposter.Instance());
        await using var _ = app;

        var response = await client.PutAsJsonAsync("/vpn/profile", new { id = "nl-ams" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ErrorBody>();
        Assert.Contains("'vpn'", body!.Error);
    }

    [Fact]
    public async Task PutProfile_KnownId_WritesTheSelectionFile_AndReturnsAccepted()
    {
        var settings = Settings();
        File.WriteAllText(Path.Combine(settings.VpnProfilesDirectory!, "nl-ams.ovpn"), "client\nremote nl.example 1194\n");
        File.WriteAllText(Path.Combine(settings.VpnStateDir, "status"), "profile=de-fra\npending=\nerror=\n");
        var catalog = new VpnProfileCatalog(settings, NullLogger<VpnProfileCatalog>.Instance);

        var (client, app) = await HostAsync(settings, catalog);
        await using var _ = app;

        var response = await client.PutAsJsonAsync("/vpn/profile", new { id = " nl-ams " });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        // The wish is recorded (trimmed); the status still reports what the supervisor runs right now.
        Assert.Equal("nl-ams\n", File.ReadAllText(settings.VpnSelectionFile));
        var body = await response.Content.ReadFromJsonAsync<StatusBody>();
        Assert.False(body!.Connected);
        Assert.Equal("de-fra", body.Profile);
    }

    [Fact]
    public async Task GetVpn_CarriesTheSupervisorTrio()
    {
        var settings = Settings();
        File.WriteAllText(Path.Combine(settings.VpnStateDir, "status"), "profile=nl-ams\npending=de-fra\nerror=openvpn exited: AUTH_FAILED\n");
        var catalog = new VpnProfileCatalog(settings, NullLogger<VpnProfileCatalog>.Instance);

        var (client, app) = await HostAsync(settings, catalog);
        await using var _ = app;

        var body = await client.GetFromJsonAsync<StatusBody>("/vpn");

        Assert.Equal("nl-ams", body!.Profile);
        Assert.Equal("de-fra", body.PendingProfile);
        Assert.Equal("openvpn exited: AUTH_FAILED", body.LastError);
    }

    private sealed record ProfilesBody(string? Active, List<ProfileBody> Profiles);

    private sealed record ProfileBody(string Id, string? Remote);

    private sealed record StatusBody(bool Connected, string? Profile, string? PendingProfile, string? LastError);

    private sealed record ErrorBody(string Error);
}
