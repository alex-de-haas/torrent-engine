// Torrent Engine control API.
//
// A MonoTorrent engine behind the OpenVPN killswitch (docker/entrypoint.sh) exposed over an
// HTTP/SSE control API for other Hosty apps to drive downloads. See README.

using TorrentEngine.Api.Api;
using TorrentEngine.Api.Realtime;
using TorrentEngine.Api.Torrents;
using TorrentEngine.Api.Vpn;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton(TorrentEngineSettings.FromConfiguration(builder.Configuration, builder.Environment.ContentRootPath));

// The engine is a single instance that is also the hosted service (starts/stops the ClientEngine)
// and the ITorrentEngine the API + broadcaster resolve.
builder.Services.AddSingleton<MonoTorrentEngine>();
builder.Services.AddSingleton<ITorrentEngine>(sp => sp.GetRequiredService<MonoTorrentEngine>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<MonoTorrentEngine>());

// VPN tunnel status: a singleton (resolved by the /vpn endpoint and the broadcaster) that is also a
// hosted service running the poll loop. The exit-IP check uses the default HttpClient factory.
builder.Services.AddHttpClient();
builder.Services.AddSingleton<VpnStatusMonitor>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<VpnStatusMonitor>());

builder.Services.AddSingleton<TorrentEventStream>();
builder.Services.AddHostedService<TorrentProgressBroadcaster>();

// Pauses downloads while the tunnel is down (and resumes them when it returns), so they don't churn
// against the killswitch and the UI shows a clean "paused — VPN down" state.
builder.Services.AddHostedService<VpnDownloadGate>();

var app = builder.Build();

// Liveness — also used by a consumer to gate readiness while the VPN tunnel comes up.
app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

// Current VPN tunnel status (a consumer seeds this on connect, then receives `vpn` SSE events).
app.MapGet("/vpn", (VpnStatusMonitor vpn) => Results.Ok(vpn.GetStatus()));

app.MapTorrentEndpoints();

app.Run();
