using TorrentEngine.Api.Torrents;

namespace TorrentEngine.Api.Vpn;

/// <summary>One OpenVPN profile from the operator's profiles folder: its id (the file name without the
/// extension) and the host[:port] of its first <c>remote</c> line — enough for a picker to label it, and
/// never the file contents.</summary>
public sealed record VpnProfileInfo(string Id, string? Remote);

/// <summary>What the entrypoint's supervisor last published (<c>{VPN_STATE_DIR}/status</c>): the profile it
/// runs, the one it is switching to, and its last failure. <see cref="Empty"/> outside the container.</summary>
public sealed record VpnSupervisorStatus(string? Profile, string? PendingProfile, string? LastError)
{
    public static readonly VpnSupervisorStatus Empty = new(null, null, null);
}

public enum VpnProfileLookup
{
    Found,

    /// <summary>Not a bare file name: empty, a path separator, a leading dot, or a control character.</summary>
    Malformed,

    /// <summary>Well-formed, but no <c>{id}.ovpn</c> / <c>{id}.conf</c> in the folder — or the file is a
    /// symlink that leaves the folder, which is treated the same way.</summary>
    NotFound,

    /// <summary>No profiles folder is injected (<c>HOSTY_MOUNT_VPN</c> unset — a run outside Hosty).</summary>
    NotConfigured,
}

/// <summary>The engine's read-only view of the operator's OpenVPN profiles plus the one write it makes:
/// recording which profile should run. Process and firewall work stays in the entrypoint's supervisor
/// (root, <c>NET_ADMIN</c>); this only lists files and exchanges two small files with that loop.</summary>
public interface IVpnProfileCatalog
{
    /// <summary>Whether a profiles folder is injected at all.</summary>
    bool IsConfigured { get; }

    /// <summary>Every profile in the folder, listed live and sorted by id. Empty when unconfigured or when the
    /// folder is missing.</summary>
    IReadOnlyList<VpnProfileInfo> ListProfiles();

    /// <summary>Validates an id the way the supervisor will: bare file name, existing file inside the folder.</summary>
    VpnProfileLookup Lookup(string? id);

    /// <summary>The supervisor's last published status; <see cref="VpnSupervisorStatus.Empty"/> when it has not
    /// written one (or outside the container).</summary>
    VpnSupervisorStatus ReadSupervisorStatus();

    /// <summary>Records <paramref name="id"/> as the profile that should run. The supervisor picks the file up
    /// within seconds, and it is honoured again on the next start (it lives under the app data dir).</summary>
    void Select(string id);
}

public sealed class VpnProfileCatalog(TorrentEngineSettings settings, ILogger<VpnProfileCatalog> logger) : IVpnProfileCatalog
{
    // .ovpn first so it wins when both extensions exist for one id (ListProfiles dedupes, TryResolve probes in order).
    private static readonly string[] ProfileExtensions = [".ovpn", ".conf"];
    private static readonly char[] WordSeparators = [' ', '\t'];

    public bool IsConfigured => settings.VpnProfilesDirectory is not null;

    public IReadOnlyList<VpnProfileInfo> ListProfiles()
    {
        var root = settings.VpnProfilesDirectory;
        if (root is null || !Directory.Exists(root))
        {
            return [];
        }

        // Ordinal, not culture-aware: the entrypoint's automatic pick is "first by name" in the C locale, and the
        // list a picker shows should agree with it about what comes first.
        var byId = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var fullRoot = Path.GetFullPath(root);
        var resolvedRoot = ResolveDirectory(fullRoot);
        try
        {
            foreach (var extension in ProfileExtensions)
            {
                foreach (var path in Directory.EnumerateFiles(root, "*" + extension, SearchOption.TopDirectoryOnly))
                {
                    if (!path.EndsWith(extension, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    // The same trust boundary as Lookup, so the list never advertises an entry a selection rejects.
                    var id = Path.GetFileNameWithoutExtension(path);
                    var candidate = Path.GetFullPath(path);
                    if (IsValidId(id) && IsWithinFolder(fullRoot, resolvedRoot, candidate, id))
                    {
                        byId.TryAdd(id, candidate);
                    }
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Could not list VPN profiles in {Directory}.", root);
            return [];
        }

        return byId.Select(entry => new VpnProfileInfo(entry.Key, ReadRemote(entry.Value))).ToArray();
    }

    public VpnProfileLookup Lookup(string? id)
    {
        if (!IsConfigured)
        {
            return VpnProfileLookup.NotConfigured;
        }

        if (!IsValidId(id))
        {
            return VpnProfileLookup.Malformed;
        }

        return TryResolve(id!) is not null ? VpnProfileLookup.Found : VpnProfileLookup.NotFound;
    }

    public VpnSupervisorStatus ReadSupervisorStatus()
    {
        var file = Path.Combine(settings.VpnStateDir, "status");
        try
        {
            if (!File.Exists(file))
            {
                return VpnSupervisorStatus.Empty;
            }

            string? profile = null, pending = null, error = null;
            foreach (var line in File.ReadLines(file))
            {
                var separator = line.IndexOf('=');
                if (separator <= 0)
                {
                    continue;
                }

                var value = line[(separator + 1)..].Trim();
                if (value.Length == 0)
                {
                    continue;
                }

                switch (line[..separator].Trim())
                {
                    case "profile": profile = value; break;
                    case "pending": pending = value; break;
                    case "error": error = value; break;
                }
            }

            return new VpnSupervisorStatus(profile, pending, error);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(exception, "Could not read the VPN supervisor status at {File}.", file);
            return VpnSupervisorStatus.Empty;
        }
    }

    public void Select(string id)
    {
        var file = settings.VpnSelectionFile;
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        // Temp + rename so the supervisor (which re-reads the file every couple of seconds) never sees a torn write.
        var temp = file + ".tmp";
        File.WriteAllText(temp, id + "\n");
        File.Move(temp, file, overwrite: true);
    }

    /// <summary>A bare file name: no path separators, no leading dot (hidden files and <c>..</c>), no control
    /// characters, no surrounding whitespace. Mirrors the entrypoint's <c>valid_id</c>.</summary>
    internal static bool IsValidId(string? id) =>
        !string.IsNullOrWhiteSpace(id)
        && id.Length <= 255
        && id[0] != '.'
        && id.Trim().Length == id.Length
        && !id.Any(c => c is '/' or '\\' || char.IsControl(c));

    /// <summary>The profile file for <paramref name="id"/>, or <c>null</c> when there is none inside the folder.</summary>
    private string? TryResolve(string id)
    {
        var root = Path.GetFullPath(settings.VpnProfilesDirectory!);
        var resolvedRoot = ResolveDirectory(root);
        foreach (var extension in ProfileExtensions)
        {
            var candidate = Path.GetFullPath(Path.Combine(root, id + extension));
            if (File.Exists(candidate) && IsWithinFolder(root, resolvedRoot, candidate, id))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>The folder is the trust boundary: a file counts only if it lies inside it, and a symlink only if its
    /// final target does too (the entrypoint applies the same rule with <c>realpath</c>). Shared by the listing and
    /// the lookup, so the two can never disagree about a file.</summary>
    private bool IsWithinFolder(string root, string resolvedRoot, string candidate, string id)
    {
        if (!IsInside(root, candidate))
        {
            return false;
        }

        try
        {
            var target = new FileInfo(candidate).ResolveLinkTarget(returnFinalTarget: true);
            if (target is not null && !IsInside(root, target.FullName) && !IsInside(resolvedRoot, target.FullName))
            {
                logger.LogWarning("VPN profile '{Id}' is a symlink leaving the profiles folder; ignoring it.", id);
                return false;
            }

            return true;
        }
        catch (IOException)
        {
            return false; // A dangling or unreadable link is not a profile.
        }
    }

    private static string ResolveDirectory(string path)
    {
        try
        {
            return Directory.ResolveLinkTarget(path, returnFinalTarget: true)?.FullName ?? path;
        }
        catch (IOException)
        {
            return path;
        }
    }

    private static bool IsInside(string root, string path) =>
        string.Equals(path, root, StringComparison.Ordinal)
        || path.StartsWith(root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    /// <summary>host[:port] from the first <c>remote &lt;host&gt; [port] [proto]</c> line, or <c>null</c>. CRLF and
    /// surrounding whitespace are tolerated; <c>remote-random</c>, comments and the like are not <c>remote</c>.</summary>
    private string? ReadRemote(string path)
    {
        try
        {
            foreach (var raw in File.ReadLines(path))
            {
                var line = raw.Trim();
                if (!line.StartsWith("remote", StringComparison.Ordinal))
                {
                    continue;
                }

                var words = line.Split(WordSeparators, StringSplitOptions.RemoveEmptyEntries);
                if (words.Length < 2 || words[0] != "remote")
                {
                    continue;
                }

                return words.Length >= 3 && int.TryParse(words[2], out var port) ? $"{words[1]}:{port}" : words[1];
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(exception, "Could not read the remote of VPN profile {Path}.", path);
        }

        return null;
    }
}
