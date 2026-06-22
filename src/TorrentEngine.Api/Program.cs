// Torrent Engine control API.
//
// A MonoTorrent engine behind the OpenVPN killswitch (docker/entrypoint.sh) exposed over an
// HTTP/SSE control API for other Hosty apps to drive downloads. See README.

using TorrentEngine.Api.Api;
using TorrentEngine.Api.Realtime;
using TorrentEngine.Api.Torrents;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton(TorrentEngineSettings.FromConfiguration(builder.Configuration, builder.Environment.ContentRootPath));

// The engine is a single instance that is also the hosted service (starts/stops the ClientEngine)
// and the ITorrentEngine the API + broadcaster resolve.
builder.Services.AddSingleton<MonoTorrentEngine>();
builder.Services.AddSingleton<ITorrentEngine>(sp => sp.GetRequiredService<MonoTorrentEngine>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<MonoTorrentEngine>());

builder.Services.AddSingleton<TorrentEventStream>();
builder.Services.AddHostedService<TorrentProgressBroadcaster>();

var app = builder.Build();

// Liveness — also used by a consumer to gate readiness while the VPN tunnel comes up.
app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

app.MapTorrentEndpoints();

app.Run();
