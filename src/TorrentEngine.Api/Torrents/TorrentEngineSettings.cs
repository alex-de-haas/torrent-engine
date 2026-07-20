namespace TorrentEngine.Api.Torrents;

/// <summary>
/// Engine configuration resolved once from the <c>HOSTY_*</c> / <c>TORRENT_*</c> environment Hosty
/// injects. The app never hard-codes ports or paths.
/// </summary>
public sealed class TorrentEngineSettings
{
    /// <summary>App data dir (fast-resume + magnet metadata cache live here).</summary>
    public required string AppDataDir { get; init; }

    /// <summary>Downloads roots keyed by their Hosty mount label. A relative request <c>savePath</c> is
    /// resolved against the root the request selects by label (<see cref="ResolveSaveDirectory"/>) so a
    /// consumer can move completed files on the same filesystem. Several roots come from a <c>multiple</c>
    /// <c>HOSTY_MOUNT_DOWNLOADS</c> mount — one host path per catalog filesystem, the operator binding the
    /// same host paths (with the same labels) the consumer uses as catalog roots. A standalone run with no
    /// mount injected gets a single unlabeled fallback root at <c>{AppDataDir}/downloads</c>.</summary>
    public required IReadOnlyDictionary<string, string> DownloadsRoots { get; init; }

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

    /// <summary>Maximum number of concurrently-registered torrents; 0 = unlimited. A cap bounds how much an
    /// (as-yet-unauthenticated) caller on the docker bridge can make the engine store.</summary>
    public int MaxActiveTorrents { get; init; }

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
        var downloadsRoots = ParseDownloadsRoots(Read("HOSTY_MOUNT_DOWNLOADS"), appDataDir);

        return new TorrentEngineSettings
        {
            AppDataDir = appDataDir,
            DownloadsRoots = downloadsRoots,
            Port = ReadInt("TORRENT_PORT", ReadInt("HOSTY_PORT_TORRENT", 6881)),
            BindAddress = Read("TORRENT_BIND_ADDRESS"),
            EnablePortMapping = ReadBool("TORRENT_ENABLE_PORT_MAPPING", false),
            MaxDownloadSpeed = ReadInt("TORRENT_MAX_DOWNLOAD_SPEED", 0),
            MaxUploadSpeed = ReadInt("TORRENT_MAX_UPLOAD_SPEED", 0),
            MaxActiveTorrents = ReadInt("TORRENT_MAX_ACTIVE", 0),
            VpnInterface = Read("VPN_INTERFACE") ?? "tun0",
            VpnExitCheckEnabled = ReadBool("VPN_EXIT_IP_CHECK", true),
            VpnExitCheckUrl = Read("VPN_EXIT_IP_CHECK_URL") ?? "https://ipinfo.io/json",
        };
    }

    /// <summary>Parses <c>HOSTY_MOUNT_DOWNLOADS</c> — a comma-joined list of <c>label=path</c> entries (Core
    /// injects container paths under docker, host paths under localCommand) — into the label→root map. A host
    /// path may itself contain <c>'='</c>, so each entry is split on the <b>first</b> <c>'='</c> only; an entry
    /// with no <c>'='</c> (an older Core that injected bare paths) or a blank explicit label falls back to the
    /// path's base name as its label. Matching is case-insensitive, and an entry that still has no usable label
    /// (e.g. a bare filesystem root) is skipped rather than stored under an unreachable empty key — Core never
    /// injects an empty label. With no mount injected (standalone/dev) a single unlabeled fallback root is used
    /// so the engine still runs.</summary>
    internal static IReadOnlyDictionary<string, string> ParseDownloadsRoots(string? raw, string appDataDir)
    {
        var roots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(raw))
        {
            foreach (var entry in raw.Split([',', '\n', '\r', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var separator = entry.IndexOf('=');
                var path = (separator >= 0 ? entry[(separator + 1)..] : entry).Trim();
                if (path.Length == 0)
                {
                    continue;
                }

                var label = separator >= 0 ? entry[..separator].Trim() : string.Empty;
                if (label.Length == 0)
                {
                    label = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                }

                if (label.Length == 0)
                {
                    continue;
                }

                roots[label] = Path.GetFullPath(path);
            }
        }

        if (roots.Count == 0)
        {
            roots[string.Empty] = Path.GetFullPath(Path.Combine(appDataDir, "downloads"));
        }

        return roots;
    }

    /// <summary>Resolves a request <c>savePath</c> against the downloads root selected by
    /// <paramref name="mountLabel"/> and guarantees the result stays inside it — a traversal like
    /// <c>../..</c> (or an absolute path outside the root) is rejected with <see cref="ArgumentException"/> so
    /// a download can never be written off-mount. When the engine has a single root the label is optional;
    /// when it has several, an unknown or empty label is rejected so a download is never written to the wrong
    /// filesystem.</summary>
    public string ResolveSaveDirectory(string? mountLabel, string? savePath)
    {
        var root = Path.GetFullPath(ResolveRoot(mountLabel));
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

    private string ResolveRoot(string? mountLabel)
    {
        var label = mountLabel?.Trim();
        if (!string.IsNullOrEmpty(label))
        {
            if (DownloadsRoots.TryGetValue(label, out var root))
            {
                return root;
            }

            throw new ArgumentException(
                $"No downloads mount labeled '{label}'. Bind the same host path into this app's 'downloads' mount with label '{label}' (configured: {FormatLabels()}).",
                nameof(mountLabel));
        }

        // No label is only unambiguous with a single root (one mount, or the standalone fallback).
        if (DownloadsRoots.Count == 1)
        {
            return DownloadsRoots.Values.First();
        }

        throw new ArgumentException(
            $"A mountLabel is required: the engine has multiple downloads mounts (configured: {FormatLabels()}).",
            nameof(mountLabel));
    }

    private string FormatLabels() =>
        string.Join(", ", DownloadsRoots.Keys.Select(key => key.Length == 0 ? "(default)" : key));
}
