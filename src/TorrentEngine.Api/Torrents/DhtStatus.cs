namespace TorrentEngine.Api.Torrents;

/// <summary>
/// Snapshot of the BitTorrent DHT, so an operator can tell a DHT that is *enabled but not working* from one
/// that is simply off or idle — the three look identical from the outside otherwise.
/// </summary>
/// <param name="Enabled">The <c>TORRENT_ENABLE_DHT</c> setting. Read from configuration rather than from the
/// engine: MonoTorrent hands out a null-object DHT reporting <c>NotReady</c> when DHT is disabled, so the
/// engine alone cannot distinguish "off" from "broken".</param>
/// <param name="Running">Whether DHT is actually running: enabled *and* an engine exists. The engine is
/// recycled when no torrent is registered, so an idle app reports <c>false</c> — that is normal, not a
/// fault.</param>
/// <param name="State">MonoTorrent's <c>DhtState</c> (<c>NotReady</c> / <c>Initialising</c> / <c>Ready</c>)
/// while running; <c>null</c> otherwise, because a DHT that is not running has no state to report.
/// <c>Initialising</c> is a healthy start-up, and only <c>NotReady</c> means it failed to come up — a
/// consumer should derive "enabled but not working" as <c>Enabled &amp;&amp; Running &amp;&amp; State ==
/// "NotReady"</c>, never as <c>State != "Ready"</c>.</param>
/// <param name="NodeCount">Size of the routing table; <c>0</c> when not running. A running DHT stuck at
/// <c>0</c> is the signature of a bootstrap that never found a peer.</param>
public sealed record DhtStatus(bool Enabled, bool Running, string? State, int NodeCount);
