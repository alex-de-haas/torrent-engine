using System.Text.Json;
using System.Threading.Channels;
using TorrentEngine.Api.Realtime;
using TorrentEngine.Api.Torrents;

namespace TorrentEngine.Api.Api;

/// <summary>Maps the control API: add/list/inspect/pause/resume/stop/remove downloads, plus the SSE
/// event stream. Engine records (<see cref="TorrentDescriptor"/>/<see cref="TorrentSnapshot"/>) are
/// returned directly.</summary>
public static class TorrentEndpoints
{
    /// <summary>How often an otherwise-idle SSE stream emits a keepalive comment.</summary>
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(20);

    public static void MapTorrentEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/downloads", async (AddDownloadRequest request, ITorrentEngine engine, TorrentEngineSettings settings, CancellationToken ct) =>
        {
            if (!TryResolveSource(request, out var source, out var error))
            {
                return Results.BadRequest(new ErrorResponse(error!));
            }

            // Validate the source (and read its info hash) up front so a bad magnet/.torrent is a 400,
            // and a re-add of an already-registered torrent is a 409, rather than a 500 from the engine.
            // Torrent.Load throws TorrentException/BEncodingException on valid-base64/garbage-bencode input;
            // those are bad input (400), not server faults.
            TorrentDescriptor inspected;
            try
            {
                inspected = engine.Inspect(source!);
            }
            catch (Exception exception) when (
                exception is ArgumentException or MonoTorrent.TorrentException or MonoTorrent.BEncoding.BEncodingException)
            {
                return Results.BadRequest(new ErrorResponse(exception.Message));
            }

            if (engine.GetSnapshot(inspected.InfoHash) is not null)
            {
                return Results.Conflict(new ErrorResponse($"Torrent {inspected.InfoHash} is already registered."));
            }

            if (settings.MaxActiveTorrents > 0 && engine.GetAllSnapshots().Count >= settings.MaxActiveTorrents)
            {
                return Results.Conflict(new ErrorResponse(
                    $"Active torrent limit reached ({settings.MaxActiveTorrents}). Remove a download before adding another."));
            }

            if (request.MaxDownloadRate is < 0 || request.MaxUploadRate is < 0)
            {
                return Results.BadRequest(new ErrorResponse("maxDownloadRate/maxUploadRate must be >= 0 (0 = unlimited)."));
            }

            string saveDirectory;
            try
            {
                saveDirectory = settings.ResolveSaveDirectory(request.MountLabel, request.SavePath);
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new ErrorResponse(exception.Message));
            }

            var limits = new TorrentLimits(
                Math.Max(0, request.MaxDownloadRate ?? settings.MaxDownloadSpeed),
                Math.Max(0, request.MaxUploadRate ?? settings.MaxUploadSpeed));

            try
            {
                var descriptor = await engine.AddAsync(source!, saveDirectory, limits, request.AutoStart ?? true, ct);
                return Results.Ok(descriptor);
            }
            catch (DuplicateTorrentException exception)
            {
                // A concurrent add of the same hash raced past the snapshot pre-check above.
                return Results.Conflict(new ErrorResponse(exception.Message));
            }
        });

        app.MapGet("/downloads", (ITorrentEngine engine) => Results.Ok(engine.GetAllSnapshots()));

        app.MapGet("/downloads/{infoHash}", (string infoHash, ITorrentEngine engine) =>
            engine.GetSnapshot(infoHash) is { } snapshot ? Results.Ok(snapshot) : Results.NotFound());

        app.MapGet("/downloads/{infoHash}/files", (string infoHash, ITorrentEngine engine) =>
            engine.GetFiles(infoHash) is { } files ? Results.Ok(files) : Results.NotFound());

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

        app.MapDelete("/downloads/{infoHash}", async (string infoHash, ITorrentEngine engine, CancellationToken ct, bool deleteFiles = false) =>
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
                while (true)
                {
                    // Bound each read so an idle stream still emits a keepalive comment every ~20s: a stream
                    // that sends zero bytes can be silently dropped by intermediary proxies otherwise.
                    using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    idle.CancelAfter(KeepAliveInterval);

                    TorrentEvent evt;
                    try
                    {
                        evt = await reader.ReadAsync(idle.Token);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        // Idle timeout, not a client disconnect — send an SSE comment to keep the pipe warm.
                        await context.Response.WriteAsync(": ping\n\n", ct);
                        await context.Response.Body.FlushAsync(ct);
                        continue;
                    }

                    var data = JsonSerializer.Serialize(evt, AppJsonSerializerContext.Default.TorrentEvent);
                    await context.Response.WriteAsync($"event: {evt.Type}\ndata: {data}\n\n", ct);
                    await context.Response.Body.FlushAsync(ct);
                }
            }
            catch (OperationCanceledException)
            {
                // Client disconnected.
            }
            catch (ChannelClosedException)
            {
                // Subscriber channel completed (shutdown/unsubscribe).
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

        var hasMagnet = !string.IsNullOrWhiteSpace(request.Magnet);
        var hasTorrent = !string.IsNullOrWhiteSpace(request.TorrentBase64);
        if (hasMagnet && hasTorrent)
        {
            error = "Provide exactly one of 'magnet' or 'torrentBase64', not both.";
            return false;
        }

        if (hasMagnet)
        {
            source = new TorrentSource.Magnet(request.Magnet!);
            return true;
        }

        if (hasTorrent)
        {
            try
            {
                source = new TorrentSource.File(Convert.FromBase64String(request.TorrentBase64!), null);
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
