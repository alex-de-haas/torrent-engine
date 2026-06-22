// Torrent Engine control API.
//
// Skeleton: the HTTP/SSE surface is declared here; endpoints return 501 until the
// MonoTorrent engine is ported from media-server (MonoTorrentEngine.cs) and wired in.
// The engine itself runs behind the OpenVPN killswitch set up by docker/entrypoint.sh.

var builder = WebApplication.CreateBuilder(args);

var app = builder.Build();

// Liveness — also used by the consumer to gate readiness while the tunnel comes up.
app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

// --- Control API (see README "Control API"). Stubbed pending the engine port. ---
static IResult NotYet() => Results.StatusCode(StatusCodes.Status501NotImplemented);

app.MapPost("/downloads", () => NotYet());                       // { source, savePath, limits, keepSeeding } -> { infoHash }
app.MapGet("/downloads", () => NotYet());                        // list snapshots
app.MapGet("/downloads/{infoHash}", (string infoHash) => NotYet());
app.MapPost("/downloads/{infoHash}/pause", (string infoHash) => NotYet());
app.MapPost("/downloads/{infoHash}/resume", (string infoHash) => NotYet());
app.MapPost("/downloads/{infoHash}/stop", (string infoHash) => NotYet());
app.MapDelete("/downloads/{infoHash}", (string infoHash, bool deleteFiles = false) => NotYet());
app.MapGet("/events", () => NotYet());                           // SSE: progress/metadata/completed/errored

app.Run();
