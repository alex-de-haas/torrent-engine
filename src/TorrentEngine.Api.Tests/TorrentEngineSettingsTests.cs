using Microsoft.Extensions.Configuration;
using TorrentEngine.Api.Torrents;

namespace TorrentEngine.Api.Tests;

public sealed class TorrentEngineSettingsTests
{
    private static string Full(params string[] segments) => Path.GetFullPath(Path.Combine(segments));

    private static TorrentEngineSettings WithRoots(string? raw, string appDataDir = "/tmp/torrent-engine") =>
        new() { AppDataDir = appDataDir, DownloadsRoots = TorrentEngineSettings.ParseDownloadsRoots(raw, appDataDir) };

    // ---- ParseDownloadsRoots ----

    [Fact]
    public void ParseDownloadsRoots_NoMount_FallsBackToSingleUnlabeledRoot()
    {
        var appDataDir = Path.Combine(Path.GetTempPath(), "te-app");

        var roots = TorrentEngineSettings.ParseDownloadsRoots(null, appDataDir);

        var entry = Assert.Single(roots);
        Assert.Equal(string.Empty, entry.Key);
        Assert.Equal(Full(appDataDir, "downloads"), entry.Value);
    }

    [Fact]
    public void ParseDownloadsRoots_ParsesCommaJoinedLabelPathEntries()
    {
        var movies = Path.Combine(Path.GetTempPath(), "dl", "movies");
        var tv = Path.Combine(Path.GetTempPath(), "dl", "tv");

        var roots = TorrentEngineSettings.ParseDownloadsRoots($"movies={movies},tv={tv}", "/tmp/te");

        Assert.Equal(2, roots.Count);
        Assert.Equal(Path.GetFullPath(movies), roots["movies"]);
        Assert.Equal(Path.GetFullPath(tv), roots["tv"]);
    }

    [Fact]
    public void ParseDownloadsRoots_SplitsOnFirstEquals_WhenPathContainsEquals()
    {
        // A host path may legally contain '=', so only the first '=' separates label from path.
        var roots = TorrentEngineSettings.ParseDownloadsRoots("movies=/srv/a=x", "/tmp/te");

        var entry = Assert.Single(roots);
        Assert.Equal("movies", entry.Key);
        Assert.Equal(Path.GetFullPath("/srv/a=x"), entry.Value);
    }

    [Fact]
    public void ParseDownloadsRoots_NoLabel_FallsBackToBaseName()
    {
        // Defensive: an older Core that injects a bare path keys it by its base name.
        var roots = TorrentEngineSettings.ParseDownloadsRoots("/srv/downloads/movies", "/tmp/te");

        var entry = Assert.Single(roots);
        Assert.Equal("movies", entry.Key);
        Assert.Equal(Path.GetFullPath("/srv/downloads/movies"), entry.Value);
    }

    [Fact]
    public void ParseDownloadsRoots_BlankExplicitLabel_FallsBackToBaseName()
    {
        // A malformed `=/path` entry has a blank explicit label; derive it from the base name instead of
        // storing an unreachable empty key. (Core never injects an empty label.)
        var roots = TorrentEngineSettings.ParseDownloadsRoots("=/srv/downloads/anime", "/tmp/te");

        var entry = Assert.Single(roots);
        Assert.Equal("anime", entry.Key);
    }

    // ---- ResolveSaveDirectory ----

    [Fact]
    public void ResolveSaveDirectory_SingleRoot_NullLabel_ResolvesUnderIt()
    {
        var root = Path.Combine(Path.GetTempPath(), "dl", "only");
        var settings = WithRoots($"only={root}");

        var resolved = settings.ResolveSaveDirectory(mountLabel: null, savePath: ".incoming/abc");

        Assert.Equal(Full(root, ".incoming", "abc"), resolved);
    }

    [Fact]
    public void ResolveSaveDirectory_PicksRootByLabel()
    {
        var movies = Path.Combine(Path.GetTempPath(), "dl", "movies");
        var tv = Path.Combine(Path.GetTempPath(), "dl", "tv");
        var settings = WithRoots($"movies={movies},tv={tv}");

        var resolved = settings.ResolveSaveDirectory("tv", "Anime/.incoming/abc");

        Assert.Equal(Full(tv, "Anime", ".incoming", "abc"), resolved);
    }

    [Fact]
    public void ResolveSaveDirectory_MatchesLabelCaseInsensitively()
    {
        var movies = Path.Combine(Path.GetTempPath(), "dl", "movies");
        var tv = Path.Combine(Path.GetTempPath(), "dl", "tv");
        var settings = WithRoots($"movies={movies},tv={tv}");

        var resolved = settings.ResolveSaveDirectory("MOVIES", ".incoming/abc");

        Assert.Equal(Full(movies, ".incoming", "abc"), resolved);
    }

    [Fact]
    public void ResolveSaveDirectory_NullSavePath_ReturnsRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "dl", "movies");
        var settings = WithRoots($"movies={root}");

        Assert.Equal(Path.GetFullPath(root), settings.ResolveSaveDirectory("movies", savePath: null));
    }

    [Fact]
    public void ResolveSaveDirectory_UnknownLabel_Throws()
    {
        var settings = WithRoots($"movies={Path.Combine(Path.GetTempPath(), "dl", "movies")}");

        var error = Assert.Throws<ArgumentException>(() => settings.ResolveSaveDirectory("tv", ".incoming/abc"));
        Assert.Contains("tv", error.Message);
    }

    [Fact]
    public void ResolveSaveDirectory_MultipleRoots_NoLabel_Throws()
    {
        var movies = Path.Combine(Path.GetTempPath(), "dl", "movies");
        var tv = Path.Combine(Path.GetTempPath(), "dl", "tv");
        var settings = WithRoots($"movies={movies},tv={tv}");

        Assert.Throws<ArgumentException>(() => settings.ResolveSaveDirectory(mountLabel: null, savePath: ".incoming/abc"));
    }

    [Fact]
    public void ResolveSaveDirectory_RejectsTraversalOutsideRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "dl", "movies");
        var settings = WithRoots($"movies={root}");

        Assert.Throws<ArgumentException>(() => settings.ResolveSaveDirectory("movies", "../../etc/passwd"));
    }

    // ---- TORRENT_ENABLE_DHT ----

    private static TorrentEngineSettings FromEnvironment(params (string Key, string Value)[] entries) =>
        TorrentEngineSettings.FromConfiguration(
            new ConfigurationBuilder()
                .AddInMemoryCollection(entries.Select(e => new KeyValuePair<string, string?>(e.Key, e.Value)))
                .Build(),
            contentRoot: Path.GetTempPath());

    [Fact]
    public void EnableDht_DefaultsToTrue_SoAnUpgradeChangesNothing() =>
        Assert.True(FromEnvironment().EnableDht);

    [Theory]
    [InlineData("false", false)]
    [InlineData("False", false)]
    [InlineData("true", true)]
    public void EnableDht_ReadsTheConfiguredValue(string configured, bool expected) =>
        Assert.Equal(expected, FromEnvironment(("TORRENT_ENABLE_DHT", configured)).EnableDht);

    [Theory]
    [InlineData("yes")]
    [InlineData("0")]
    [InlineData("")]
    public void EnableDht_MalformedValue_FallsBackToTheDefault(string configured) =>
        // A typo must leave peer discovery working rather than silently turning DHT off.
        Assert.True(FromEnvironment(("TORRENT_ENABLE_DHT", configured)).EnableDht);

    // ---- ParseFirstMountPath (HOSTY_MOUNT_VPN) ----

    [Fact]
    public void ParseFirstMountPath_NoMount_IsNull()
    {
        Assert.Null(TorrentEngineSettings.ParseFirstMountPath(null));
        Assert.Null(TorrentEngineSettings.ParseFirstMountPath("   "));
        Assert.Null(TorrentEngineSettings.ParseFirstMountPath("profiles="));
    }

    [Fact]
    public void ParseFirstMountPath_TakesTheFirstBindingsPath()
    {
        Assert.Equal(Path.GetFullPath("/mnt/vpn/profiles"), TorrentEngineSettings.ParseFirstMountPath("profiles=/mnt/vpn/profiles"));
        Assert.Equal(Path.GetFullPath("/srv/a"), TorrentEngineSettings.ParseFirstMountPath("a=/srv/a,b=/srv/b"));
        // Split on the first '=' only; a bare path (older Core) is accepted as-is.
        Assert.Equal(Path.GetFullPath("/srv/a=x"), TorrentEngineSettings.ParseFirstMountPath("a=/srv/a=x"));
        Assert.Equal(Path.GetFullPath("/srv/vpn"), TorrentEngineSettings.ParseFirstMountPath("/srv/vpn"));
    }

    [Fact]
    public void FromConfiguration_ReadsTheVpnMountAndStateDir_WithDefaults()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["HOSTY_APP_DATA_DIR"] = "/data",
            ["HOSTY_MOUNT_VPN"] = "profiles=/mnt/vpn/profiles",
        }).Build();

        var settings = TorrentEngineSettings.FromConfiguration(configuration, "/app");

        Assert.Equal(Path.GetFullPath("/mnt/vpn/profiles"), settings.VpnProfilesDirectory);
        Assert.Equal("/run/vpn", settings.VpnStateDir);
        Assert.Equal(Path.Combine(Path.GetFullPath("/data"), "vpn", "active-profile"), settings.VpnSelectionFile);

        var overridden = TorrentEngineSettings.FromConfiguration(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["VPN_STATE_DIR"] = "/tmp/vpn-state" }).Build(), "/app");
        Assert.Null(overridden.VpnProfilesDirectory);
        Assert.Equal("/tmp/vpn-state", overridden.VpnStateDir);
    }
}
