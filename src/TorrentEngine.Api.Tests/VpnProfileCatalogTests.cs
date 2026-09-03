using Microsoft.Extensions.Logging.Abstractions;
using TorrentEngine.Api.Torrents;
using TorrentEngine.Api.Vpn;

namespace TorrentEngine.Api.Tests;

/// <summary>
/// <see cref="VpnProfileCatalog"/> against a real temp folder: what counts as a profile, how ids are validated
/// (the folder is the trust boundary), what the supervisor's status file parses to, and how a selection is
/// recorded. Everything the entrypoint's shell side relies on the API to agree with.
/// </summary>
public sealed class VpnProfileCatalogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "te-vpn-" + Guid.NewGuid().ToString("N"));
    private readonly string _profiles;
    private readonly string _state;
    private readonly string _data;

    public VpnProfileCatalogTests()
    {
        _profiles = Path.Combine(_root, "profiles");
        _state = Path.Combine(_root, "state");
        _data = Path.Combine(_root, "data");
        Directory.CreateDirectory(_profiles);
        Directory.CreateDirectory(_state);
        Directory.CreateDirectory(_data);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private TorrentEngineSettings Settings(string? profilesDirectory) => new()
    {
        AppDataDir = _data,
        DownloadsRoots = new Dictionary<string, string>(),
        VpnProfilesDirectory = profilesDirectory,
        VpnStateDir = _state,
    };

    private VpnProfileCatalog Catalog() => new(Settings(_profiles), NullLogger<VpnProfileCatalog>.Instance);

    private VpnProfileCatalog Unconfigured() => new(Settings(null), NullLogger<VpnProfileCatalog>.Instance);

    private string Put(string name, string content = "client\n")
    {
        var path = Path.Combine(_profiles, name);
        File.WriteAllText(path, content);
        return path;
    }

    // ---- ListProfiles ----

    [Fact]
    public void ListProfiles_ListsOnlyProfileFiles_SortedById()
    {
        Put("b.ovpn");
        Put("a.ovpn");
        Put("c.conf");
        Put("a.auth", "user\npass\n");
        Put("ca.crt");
        Put("ta.key");
        Put(".hidden.ovpn");
        Put("README.md");
        Directory.CreateDirectory(Path.Combine(_profiles, "sub"));
        File.WriteAllText(Path.Combine(_profiles, "sub", "d.ovpn"), "client\n");

        var ids = Catalog().ListProfiles().Select(p => p.Id).ToArray();

        Assert.Equal(["a", "b", "c"], ids);
    }

    [Fact]
    public void ListProfiles_OrdinalOrder_AndOvpnWinsOverConf()
    {
        // Ordinal (C-locale) order matches the entrypoint's "first by name": uppercase sorts before lowercase.
        Put("B.ovpn");
        Put("a.ovpn", "remote from-ovpn 1194\n");
        Put("a.conf", "remote from-conf 1194\n");

        var profiles = Catalog().ListProfiles();

        Assert.Equal(["B", "a"], profiles.Select(p => p.Id).ToArray());
        Assert.Equal("from-ovpn:1194", profiles.Single(p => p.Id == "a").Remote);
    }

    [Fact]
    public void ListProfiles_ExtractsTheFirstRemote_HostAndPort()
    {
        Put("full.ovpn", "# remote commented.example 1\r\nclient\r\nremote-random\r\n  remote vpn.example.com 1194 udp\r\nremote other.example 443 tcp\r\n");
        Put("host-only.ovpn", "client\nremote host-only.example\n");
        Put("no-port-number.ovpn", "remote weird.example udp\n");
        Put("none.ovpn", "client\ndev tun\n");

        var byId = Catalog().ListProfiles().ToDictionary(p => p.Id, p => p.Remote);

        Assert.Equal("vpn.example.com:1194", byId["full"]);
        Assert.Equal("host-only.example", byId["host-only"]);
        Assert.Equal("weird.example", byId["no-port-number"]);
        Assert.Null(byId["none"]);
    }

    [Fact]
    public void ListProfiles_UnconfiguredOrMissingFolder_IsEmpty()
    {
        var unconfigured = Unconfigured();
        Assert.False(unconfigured.IsConfigured);
        Assert.Empty(unconfigured.ListProfiles());

        var missing = new VpnProfileCatalog(Settings(Path.Combine(_root, "nope")), NullLogger<VpnProfileCatalog>.Instance);
        Assert.True(missing.IsConfigured);
        Assert.Empty(missing.ListProfiles());
    }

    [Fact]
    public void ListProfiles_SkipsASymlinkLeavingTheFolder_ButListsOneInside()
    {
        // The list applies the same trust boundary as Lookup, so it never advertises an entry a selection rejects.
        var outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(outside);
        var outsideProfile = Path.Combine(outside, "escape.ovpn");
        File.WriteAllText(outsideProfile, "client\nremote leaked.example 1194\n");
        File.CreateSymbolicLink(Path.Combine(_profiles, "escape.ovpn"), outsideProfile);

        var inside = Put("real.ovpn", "client\nremote real.example 1194\n");
        File.CreateSymbolicLink(Path.Combine(_profiles, "alias.ovpn"), inside);

        var profiles = Catalog().ListProfiles();

        Assert.Equal(["alias", "real"], profiles.Select(p => p.Id).ToArray());
        Assert.Equal("real.example:1194", profiles.Single(p => p.Id == "alias").Remote);
    }

    // ---- Lookup ----

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("../a")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData(".a")]
    [InlineData("..")]
    [InlineData("a\nb")]
    [InlineData(" a")]
    public void Lookup_MalformedId_IsMalformed(string? id)
    {
        Put("a.ovpn");

        Assert.Equal(VpnProfileLookup.Malformed, Catalog().Lookup(id));
    }

    [Fact]
    public void Lookup_ExistingProfiles_AreFound_ForBothExtensions()
    {
        Put("a.ovpn");
        Put("c.conf");
        Put("a.auth", "user\npass\n");

        var catalog = Catalog();

        Assert.Equal(VpnProfileLookup.Found, catalog.Lookup("a"));
        Assert.Equal(VpnProfileLookup.Found, catalog.Lookup("c"));
        Assert.Equal(VpnProfileLookup.NotFound, catalog.Lookup("zzz"));
        // Supporting files are not profiles, even though the file exists.
        Assert.Equal(VpnProfileLookup.NotFound, catalog.Lookup("ca"));
    }

    [Fact]
    public void Lookup_Unconfigured_IsNotConfigured()
    {
        Assert.Equal(VpnProfileLookup.NotConfigured, Unconfigured().Lookup("a"));
    }

    [Fact]
    public void Lookup_SymlinkLeavingTheFolder_IsNotFound_ButOneInsideIsFound()
    {
        var outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(outside);
        var outsideProfile = Path.Combine(outside, "escape.ovpn");
        File.WriteAllText(outsideProfile, "client\n");
        File.CreateSymbolicLink(Path.Combine(_profiles, "escape.ovpn"), outsideProfile);

        var inside = Put("real.ovpn");
        File.CreateSymbolicLink(Path.Combine(_profiles, "alias.ovpn"), inside);

        var catalog = Catalog();

        Assert.Equal(VpnProfileLookup.NotFound, catalog.Lookup("escape"));
        Assert.Equal(VpnProfileLookup.Found, catalog.Lookup("alias"));
    }

    // ---- ReadSupervisorStatus ----

    [Fact]
    public void ReadSupervisorStatus_MissingFile_IsEmpty()
    {
        Assert.Equal(VpnSupervisorStatus.Empty, Catalog().ReadSupervisorStatus());
    }

    [Fact]
    public void ReadSupervisorStatus_ParsesFields_IgnoringUnknownAndGarbage()
    {
        File.WriteAllText(Path.Combine(_state, "status"),
            "profile=nl-ams\r\npending=de-fra\r\nerror=openvpn exited: AUTH_FAILED\r\nupdatedAt=2026-09-03T10:00:00Z\r\nnot a pair\r\n=novalue\r\n");

        var status = Catalog().ReadSupervisorStatus();

        Assert.Equal("nl-ams", status.Profile);
        Assert.Equal("de-fra", status.PendingProfile);
        Assert.Equal("openvpn exited: AUTH_FAILED", status.LastError);
    }

    [Fact]
    public void ReadSupervisorStatus_EmptyValues_AreNull()
    {
        File.WriteAllText(Path.Combine(_state, "status"), "profile=nl-ams\npending=\nerror=\n");

        var status = Catalog().ReadSupervisorStatus();

        Assert.Equal("nl-ams", status.Profile);
        Assert.Null(status.PendingProfile);
        Assert.Null(status.LastError);
    }

    // ---- Select ----

    [Fact]
    public void Select_WritesTheSelectionFile_CreatingItsFolder_AndOverwrites()
    {
        var catalog = Catalog();
        var file = Settings(_profiles).VpnSelectionFile;
        Assert.False(File.Exists(file));

        catalog.Select("nl-ams");
        Assert.Equal("nl-ams\n", File.ReadAllText(file));
        Assert.False(File.Exists(file + ".tmp"));

        catalog.Select("de-fra");
        Assert.Equal("de-fra\n", File.ReadAllText(file));
    }
}
