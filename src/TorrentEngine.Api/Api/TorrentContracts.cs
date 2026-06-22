namespace TorrentEngine.Api.Api;

/// <summary>
/// Body of <c>POST /downloads</c>. Exactly one of <see cref="Magnet"/> / <see cref="TorrentBase64"/>
/// identifies the source. <see cref="SavePath"/> is resolved against the engine's downloads root
/// (the shared mount) when relative. Rates fall back to the engine defaults when omitted.
/// </summary>
public sealed record AddDownloadRequest(
    string? Magnet,
    string? TorrentBase64,
    string? SavePath,
    int? MaxDownloadRate,
    int? MaxUploadRate,
    bool? AutoStart);
