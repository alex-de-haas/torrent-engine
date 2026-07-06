// Torrent Engine control API.
//
// A MonoTorrent engine behind the OpenVPN killswitch (docker/entrypoint.sh) exposed over an
// HTTP/SSE control API for other Hosty apps to drive downloads. See README.

using System.Security.Cryptography;
using System.Text;
using TorrentEngine.Api.Api;
using TorrentEngine.Api.Realtime;
using TorrentEngine.Api.Telemetry;
using TorrentEngine.Api.Torrents;
using TorrentEngine.Api.Vpn;

var builder = WebApplication.CreateBuilder(args);

// Serialize/deserialize through the source-generated context (ahead of the default reflection
// resolver) so the control API works under Native AOT. See Api/AppJsonSerializerContext.cs.
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonSerializerContext.Default));

// Export traces/metrics/logs over OTLP to the Hosty collector when Core injects the OTEL_* env
// (docker runtime + observability enabled); a no-op otherwise. See Telemetry/HostyTelemetry.cs.
builder.AddHostyTelemetry();

var engineSettings = TorrentEngineSettings.FromConfiguration(builder.Configuration, builder.Environment.ContentRootPath);
builder.Services.AddSingleton(engineSettings);

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

// Interim shared-secret auth: when CONTROL_API_TOKEN is set, every request except /healthz must carry it
// in X-Api-Token. This removes the "anything on the docker bridge can drive the engine" exposure until the
// platform's app-identity tokens land. Left off (token null/empty) the API stays open, as before.
if (!string.IsNullOrEmpty(engineSettings.ControlApiToken))
{
    var expected = SHA256.HashData(Encoding.UTF8.GetBytes(engineSettings.ControlApiToken));
    app.Use(async (context, next) =>
    {
        if (context.Request.Path.StartsWithSegments("/healthz"))
        {
            await next();
            return;
        }

        var provided = SHA256.HashData(Encoding.UTF8.GetBytes(context.Request.Headers["X-Api-Token"].ToString()));
        if (!CryptographicOperations.FixedTimeEquals(provided, expected))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new ErrorResponse("Missing or invalid X-Api-Token."), AppJsonSerializerContext.Default.ErrorResponse);
            return;
        }

        await next();
    });
}

// Liveness — also used by a consumer to gate readiness while the VPN tunnel comes up.
app.MapGet("/healthz", () => Results.Ok(new HealthResponse("ok")));

// Current VPN tunnel status (a consumer seeds this on connect, then receives `vpn` SSE events).
app.MapGet("/vpn", (VpnStatusMonitor vpn) => Results.Ok(vpn.GetStatus()));

app.MapTorrentEndpoints();

app.Run();
