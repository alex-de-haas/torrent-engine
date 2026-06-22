using System.Text.Json;
using TorrentEngine.Api.Realtime;
using TorrentEngine.Api.Torrents;

namespace TorrentEngine.Api.Api;

/// <summary>Maps the control API: add/list/inspect/pause/resume/stop/remove downloads, plus the SSE
/// event stream. Engine records (<see cref="TorrentDescriptor"/>/<see cref="TorrentSnapshot"/>) are
/// returned directly.</summary>
public static class TorrentEndpoints
{
    private static readonly JsonSerializerOptions EventJson = new(JsonSerializerDefaults.Web);

    public static void MapTorrentEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/downloads", async (AddDownloadRequest request, ITorrentEngine engine, TorrentEngineSettings settings, CancellationToken ct) =>
        {
            if (!TryResolveSource(request, out var source, out var error))
            {
                return Results.BadRequest(new { error });
            }

            var saveDirectory = settings.ResolveSaveDirectory(request.SavePath);
            var limits = new TorrentLimits(
                request.MaxDownloadRate ?? settings.MaxDownloadSpeed,
                request.MaxUploadRate ?? settings.MaxUploadSpeed);

            var descriptor = await engine.AddAsync(source!, saveDirectory, limits, request.AutoStart ?? true, ct);
            return Results.Ok(descriptor);
        });

        app.MapGet("/downloads", (ITorrentEngine engine) => Results.Ok(engine.GetAllSnapshots()));

        app.MapGet("/downloads/{infoHash}", (string infoHash, ITorrentEngine engine) =>
            engine.GetSnapshot(infoHash) is { } snapshot ? Results.Ok(snapshot) : Results.NotFound());

        app.MapGet("/downloads/{infoHash}/files", (string infoHash, ITorrentEngine engine) =>
            Results.Ok(engine.GetFiles(infoHash)));

        app.MapPost("/downloads/{infoHash}/pause", async (string infoHash, ITorrentEngine engine, CancellationToken ct) =>
        {
            await engine.PauseAsync(infoHash, ct);
            return Results.NoContent();
        });

        app.MapPost("/downloads/{infoHash}/resume", async (string infoHash, ITorrentEngine engine, CancellationToken ct) =>
        {
            await engine.ResumeAsync(infoHash, ct);
            return Results.NoContent();
        });

        app.MapPost("/downloads/{infoHash}/stop", async (string infoHash, ITorrentEngine engine, CancellationToken ct) =>
        {
            await engine.StopAsync(infoHash, ct);
            return Results.NoContent();
        });

        app.MapDelete("/downloads/{infoHash}", async (string infoHash, bool deleteFiles, ITorrentEngine engine, CancellationToken ct) =>
        {
            await engine.RemoveAsync(infoHash, deleteFiles, ct);
            return Results.NoContent();
        });

        app.MapGet("/events", async (HttpContext context, TorrentEventStream stream, CancellationToken ct) =>
        {
            context.Response.Headers.ContentType = "text/event-stream";
            context.Response.Headers.CacheControl = "no-cache";
            context.Response.Headers["X-Accel-Buffering"] = "no";

            var (id, reader) = stream.Subscribe();
            try
            {
                await foreach (var evt in reader.ReadAllAsync(ct))
                {
                    var data = JsonSerializer.Serialize(evt, EventJson);
                    await context.Response.WriteAsync($"event: {evt.Type}\ndata: {data}\n\n", ct);
                    await context.Response.Body.FlushAsync(ct);
                }
            }
            catch (OperationCanceledException)
            {
                // Client disconnected.
            }
            finally
            {
                stream.Unsubscribe(id);
            }
        });
    }

    private static bool TryResolveSource(AddDownloadRequest request, out TorrentSource? source, out string? error)
    {
        source = null;
        error = null;

        if (!string.IsNullOrWhiteSpace(request.Magnet))
        {
            source = new TorrentSource.Magnet(request.Magnet);
            return true;
        }

        if (!string.IsNullOrWhiteSpace(request.TorrentBase64))
        {
            try
            {
                source = new TorrentSource.File(Convert.FromBase64String(request.TorrentBase64), null);
                return true;
            }
            catch (FormatException)
            {
                error = "torrentBase64 is not valid base64.";
                return false;
            }
        }

        error = "Either 'magnet' or 'torrentBase64' is required.";
        return false;
    }
}
