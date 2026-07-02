using System.Text.Json;
using System.Text.Json.Serialization;
using TorrentEngine.Api.Realtime;
using TorrentEngine.Api.Torrents;
using TorrentEngine.Api.Vpn;

namespace TorrentEngine.Api.Api;

/// <summary>
/// System.Text.Json source-generated metadata for every type the control API and SSE stream read or
/// write. Registered ahead of the reflection resolver in <c>Program.cs</c> so JSON keeps working under
/// Native AOT, where the reflection-based (de)serializer is unavailable. Web defaults (camelCase) match
/// the options the API used before the AOT port, so the wire format is unchanged.
/// </summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(AddDownloadRequest))]
[JsonSerializable(typeof(TorrentDescriptor))]
[JsonSerializable(typeof(TorrentSnapshot))]
[JsonSerializable(typeof(IReadOnlyList<TorrentSnapshot>))]
[JsonSerializable(typeof(TorrentFileInfo))]
[JsonSerializable(typeof(IReadOnlyList<TorrentFileInfo>))]
[JsonSerializable(typeof(VpnStatus))]
[JsonSerializable(typeof(TorrentEvent))]
[JsonSerializable(typeof(ErrorResponse))]
[JsonSerializable(typeof(HealthResponse))]
internal sealed partial class AppJsonSerializerContext : JsonSerializerContext;
