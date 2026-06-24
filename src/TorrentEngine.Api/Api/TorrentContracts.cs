namespace TorrentEngine.Api.Api;

/// <summary>
/// Body of <c>POST /downloads</c>. Exactly one of <see cref="Magnet"/> / <see cref="TorrentBase64"/>
/// identifies the source. <see cref="MountLabel"/> selects which downloads mount (by its Hosty label)
/// <see cref="SavePath"/> is resolved against when relative — required when the engine has more than one
/// downloads mount, optional when it has exactly one. Rates fall back to the engine defaults when omitted.
/// </summary>
public sealed record AddDownloadRequest(
    string? Magnet,
    string? TorrentBase64,
    string? MountLabel,
    string? SavePath,
    int? MaxDownloadRate,
    int? MaxUploadRate,
    bool? AutoStart);
