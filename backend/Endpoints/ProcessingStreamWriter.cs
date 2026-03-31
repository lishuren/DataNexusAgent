using System.Text.Json;
using DataNexus.Models;

namespace DataNexus.Endpoints;

internal static class ProcessingStreamWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task<ProcessingResult?> WriteNdjsonAsync(
        HttpResponse response,
        IAsyncEnumerable<ProcessingStreamEvent> stream,
        CancellationToken ct)
    {
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = "application/x-ndjson; charset=utf-8";
        response.Headers.CacheControl = "no-store";
        response.Headers["X-Accel-Buffering"] = "no";

        ProcessingResult? finalResult = null;

        await foreach (var streamEvent in stream.WithCancellation(ct))
        {
            if (streamEvent.Result is not null)
                finalResult = streamEvent.Result;

            var json = JsonSerializer.Serialize(streamEvent, JsonOptions);
            await response.WriteAsync(json, ct);
            await response.WriteAsync("\n", ct);
            await response.Body.FlushAsync(ct);
        }

        return finalResult;
    }
}