using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using TorrentEngine.Api.Torrents;

namespace TorrentEngine.Api.Tests;

/// <summary>
/// Direct tests of <see cref="MonoTorrentEngine.Inspect"/> — the source-validation the control API runs
/// before adding a download. Inspect only parses (it never touches the engine), so it can be exercised
/// without starting the hosted service. These pin the exception types <c>POST /downloads</c> maps to a
/// <c>400</c> (rather than a <c>500</c>) for a bad source.
/// </summary>
public sealed class MonoTorrentEngineInspectTests
{
    private static MonoTorrentEngine NewEngine()
    {
        var appData = Path.Combine(Path.GetTempPath(), "te-inspect");
        var settings = new TorrentEngineSettings
        {
            AppDataDir = appData,
            DownloadsRoots = TorrentEngineSettings.ParseDownloadsRoots(null, appData),
        };
        return new MonoTorrentEngine(settings, NullLogger<MonoTorrentEngine>.Instance);
    }

    [Fact]
    public void Inspect_GarbageTorrentBytes_ThrowsBEncodingOrTorrentException()
    {
        var engine = NewEngine();
        var garbage = new TorrentSource.File(Encoding.UTF8.GetBytes("this is not a .torrent file"), null);

        var exception = Record.Exception(() => engine.Inspect(garbage));

        Assert.NotNull(exception);
        Assert.True(
            exception is MonoTorrent.TorrentException or MonoTorrent.BEncoding.BEncodingException,
            $"expected TorrentException/BEncodingException so the API returns 400, got {exception.GetType().FullName}");
    }

    [Fact]
    public void Inspect_InvalidMagnet_ThrowsArgumentException() =>
        Assert.Throws<ArgumentException>(() => NewEngine().Inspect(new TorrentSource.Magnet("not-a-magnet")));
}
