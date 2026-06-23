namespace TorrentEngine.Api.Torrents;

/// <summary>
/// Engine configuration resolved once from the <c>HOSTY_*</c> / <c>TORRENT_*</c> environment Hosty
/// injects. The app never hard-codes ports or paths.
/// </summary>
public sealed class TorrentEngineSettings
{
    /// <summary>App data dir (fast-resume + magnet metadata cache live here).</summary>
    public required string AppDataDir { get; init; }

    /// <summary>Root the relative <c>savePath</c> of a download is resolved against — the shared
    /// downloads mount (<c>HOSTY_MOUNT_DOWNLOADS</c>) so a consumer can move completed files on the
    /// same filesystem. Falls back to <c>{AppDataDir}/downloads</c> for standalone runs.</summary>
    public required string DownloadsRoot { get; init; }

    /// <summary>Raw L4 listen port (TCP + UDP). Under an in-container VPN this is the port the engine
    /// binds inside the tunnel; the provider forwards it.</summary>
    public int Port { get; init; } = 6881;

    /// <summary>Optional bind address (e.g. a VPN interface address); null binds all interfaces.</summary>
    public string? BindAddress { get; init; }

    /// <summary>UPnP / NAT-PMP automatic port mapping. Off by default — irrelevant behind a VPN.</summary>
    public bool EnablePortMapping { get; init; }

    /// <summary>Bytes/sec; 0 = unlimited.</summary>
    public int MaxDownloadSpeed { get; init; }

    public int MaxUploadSpeed { get; init; }

    /// <summary>Name of the VPN tunnel interface the killswitch confines traffic to (see
    /// <c>docker/entrypoint.sh</c>). Used by the VPN status monitor to detect the tunnel.</summary>
    public string VpnInterface { get; init; } = "tun0";

    /// <summary>Whether the VPN status monitor performs the best-effort public exit-IP check (an
    /// outbound request over the tunnel). Disable for fully-local, no-external-call status.</summary>
    public bool VpnExitCheckEnabled { get; init; } = true;

    /// <summary>Endpoint the exit-IP check calls. A JSON body with <c>ip</c>/<c>country</c> (e.g.
    /// ipinfo.io) is preferred; a plain-text IP body is also accepted.</summary>
    public string VpnExitCheckUrl { get; init; } = "https://ipinfo.io/json";

    public static TorrentEngineSettings FromConfiguration(IConfiguration configuration, string contentRoot)
    {
        string? Read(string key) => configuration[key] is { Length: > 0 } value ? value.Trim() : null;
        int ReadInt(string key, int fallback) => int.TryParse(Read(key), out var parsed) ? parsed : fallback;
        bool ReadBool(string key, bool fallback) => bool.TryParse(Read(key), out var parsed) ? parsed : fallback;

        var appDataDir = Read("HOSTY_APP_DATA_DIR") ?? Path.Combine(contentRoot, "data");
        var downloadsRoot = Read("HOSTY_MOUNT_DOWNLOADS") ?? Path.Combine(appDataDir, "downloads");

        return new TorrentEngineSettings
        {
            AppDataDir = appDataDir,
            DownloadsRoot = downloadsRoot,
            Port = ReadInt("TORRENT_PORT", ReadInt("HOSTY_PORT_TORRENT", 6881)),
            BindAddress = Read("TORRENT_BIND_ADDRESS"),
            EnablePortMapping = ReadBool("TORRENT_ENABLE_PORT_MAPPING", false),
            MaxDownloadSpeed = ReadInt("TORRENT_MAX_DOWNLOAD_SPEED", 0),
            MaxUploadSpeed = ReadInt("TORRENT_MAX_UPLOAD_SPEED", 0),
            VpnInterface = Read("VPN_INTERFACE") ?? "tun0",
            VpnExitCheckEnabled = ReadBool("VPN_EXIT_IP_CHECK", true),
            VpnExitCheckUrl = Read("VPN_EXIT_IP_CHECK_URL") ?? "https://ipinfo.io/json",
        };
    }

    /// <summary>Resolves a request <c>savePath</c> against <see cref="DownloadsRoot"/> and guarantees the
    /// result stays inside it — a traversal like <c>../..</c> (or an absolute path outside the root) is
    /// rejected with <see cref="ArgumentException"/> so a download can never be written off-mount.</summary>
    public string ResolveSaveDirectory(string? savePath)
    {
        var root = Path.GetFullPath(DownloadsRoot);
        if (string.IsNullOrWhiteSpace(savePath))
        {
            return root;
        }

        var combined = Path.GetFullPath(Path.IsPathRooted(savePath) ? savePath : Path.Combine(root, savePath));
        var rootPrefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if (!string.Equals(combined, root, StringComparison.Ordinal) && !combined.StartsWith(rootPrefix, StringComparison.Ordinal))
        {
            throw new ArgumentException($"savePath '{savePath}' resolves outside the downloads root.", nameof(savePath));
        }

        return combined;
    }
}
