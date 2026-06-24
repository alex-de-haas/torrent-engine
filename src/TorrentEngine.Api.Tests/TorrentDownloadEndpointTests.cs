using System.Net;
using System.Net.Http.Json;
using Imposter.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using TorrentEngine.Api.Api;
using TorrentEngine.Api.Realtime;
using TorrentEngine.Api.Torrents;

[assembly: GenerateImposter(typeof(ITorrentEngine))]

namespace TorrentEngine.Api.Tests;

/// <summary>
/// Exercises <c>POST /downloads</c> against the real endpoint wiring (an in-memory TestServer hosting only
/// <see cref="TorrentEndpoints.MapTorrentEndpoints"/>) with a mocked <see cref="ITorrentEngine"/>, so the
/// MonoTorrent engine and VPN/host services never start. Covers the new <c>mountLabel</c> selection.
/// </summary>
public sealed class TorrentDownloadEndpointTests
{
    private static readonly TorrentDescriptor Descriptor = new("abc123", "Test", 100, HasMetadata: true, Files: []);

    private static TorrentEngineSettings Settings(string raw) =>
        new() { AppDataDir = "/tmp/te", DownloadsRoots = TorrentEngineSettings.ParseDownloadsRoots(raw, "/tmp/te") };

    private static async Task<(HttpClient Client, WebApplication App)> HostAsync(TorrentEngineSettings settings, ITorrentEngine engine)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(settings);
        builder.Services.AddSingleton(engine);
        // Registered so the unrelated GET /events handler's parameter resolves as a service (not an
        // inferred body) when the route table is built; this test never calls /events.
        builder.Services.AddSingleton<TorrentEventStream>();
        var app = builder.Build();
        app.MapTorrentEndpoints();
        await app.StartAsync();
        return (app.GetTestClient(), app);
    }

    [Fact]
    public async Task Post_UnknownMountLabel_ReturnsBadRequest()
    {
        var imposter = ITorrentEngine.Imposter();
        imposter.Inspect(Arg<TorrentSource>.Any()).Returns(Descriptor);
        imposter.GetSnapshot(Arg<string>.Any()).Returns((TorrentSnapshot)null!); // no torrent registered for this info hash

        var movies = Path.Combine(Path.GetTempPath(), "dl", "movies");
        var tv = Path.Combine(Path.GetTempPath(), "dl", "tv");
        var (client, app) = await HostAsync(Settings($"movies={movies},tv={tv}"), imposter.Instance());
        await using var _ = app;

        var response = await client.PostAsJsonAsync("/downloads", new
        {
            magnet = "magnet:?xt=urn:btih:abc123",
            mountLabel = "nope",
            savePath = ".incoming/abc",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ErrorBody>();
        Assert.Contains("nope", body!.Error);
    }

    [Fact]
    public async Task Post_ValidMountLabel_ReturnsDescriptor()
    {
        // The exact resolved save directory is asserted in TorrentEngineSettingsTests; here we only confirm
        // a known mountLabel is accepted (resolves, then hands off to the engine) and returns the descriptor.
        var media = Path.Combine(Path.GetTempPath(), "dl", "media");

        var imposter = ITorrentEngine.Imposter();
        imposter.Inspect(Arg<TorrentSource>.Any()).Returns(Descriptor);
        imposter.GetSnapshot(Arg<string>.Any()).Returns((TorrentSnapshot)null!); // no torrent registered for this info hash
        imposter.AddAsync(Arg<TorrentSource>.Any(), Arg<string>.Any(), Arg<TorrentLimits>.Any(), Arg<bool>.Any(), Arg<CancellationToken>.Any())
            .Returns(Task.FromResult(Descriptor));

        var (client, app) = await HostAsync(Settings($"media={media}"), imposter.Instance());
        await using var _ = app;

        var response = await client.PostAsJsonAsync("/downloads", new
        {
            magnet = "magnet:?xt=urn:btih:abc123",
            mountLabel = "media",
            savePath = "Movies/.incoming/abc",
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var descriptor = await response.Content.ReadFromJsonAsync<TorrentDescriptor>();
        Assert.Equal("abc123", descriptor!.InfoHash);
    }

    [Fact]
    public async Task Post_AlreadyRegisteredTorrent_ReturnsConflict()
    {
        var imposter = ITorrentEngine.Imposter();
        imposter.Inspect(Arg<TorrentSource>.Any()).Returns(Descriptor);
        imposter.GetSnapshot(Arg<string>.Any()).Returns(
            new TorrentSnapshot("abc123", "Test", "Downloading", false, 0, 0, 0, 0, 0, 100));

        var (client, app) = await HostAsync(Settings($"media={Path.Combine(Path.GetTempPath(), "dl", "media")}"), imposter.Instance());
        await using var _ = app;

        var response = await client.PostAsJsonAsync("/downloads", new
        {
            magnet = "magnet:?xt=urn:btih:abc123",
            mountLabel = "media",
            savePath = ".incoming/abc",
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    private sealed record ErrorBody(string Error);
}
