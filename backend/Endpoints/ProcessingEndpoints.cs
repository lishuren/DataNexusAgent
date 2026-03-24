using DataNexus.Agents;
using DataNexus.Core;
using DataNexus.Identity;
using DataNexus.Models;

namespace DataNexus.Endpoints;

public static class ProcessingEndpoints
{
    public static IEndpointRouteBuilder MapProcessingEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/process")
            .RequireAuthorization();

        group.MapPost("/", async (
            ProcessingRequest request,
            IAgentExecutionRuntime runtime,
            TaskHistoryRegistry history,
            UserContext user,
            CancellationToken ct) =>
        {
            if (!user.IsAuthenticated)
                return Results.Unauthorized();

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var result = await runtime.ProcessAsync(request, user, ct);
            sw.Stop();

            await history.RecordAsync(new TaskHistoryEntity
            {
                Summary = $"{SummarizeSource(request.InputSource)} → {request.OutputDestination}",
                AgentId = request.AgentId,
                Success = result.Success,
                Message = result.Message,
                DurationMs = sw.Elapsed.TotalMilliseconds,
                OwnerId = user.UserId,
            }, ct);

            return result.Success
                ? Results.Ok(result)
                : Results.UnprocessableEntity(result);
        });

        group.MapPost("/pipeline", async (
            PipelineRequest pipeline,
            IAgentExecutionRuntime runtime,
            TaskHistoryRegistry history,
            UserContext user,
            CancellationToken ct) =>
        {
            if (!user.IsAuthenticated)
                return Results.Unauthorized();

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var result = await runtime.RunPipelineAsync(pipeline, user, ct);
            sw.Stop();

            await history.RecordAsync(new TaskHistoryEntity
            {
                Summary = $"Pipeline: {pipeline.Name}",
                PipelineName = pipeline.Name,
                Success = result.Success,
                Message = result.Message,
                DurationMs = sw.Elapsed.TotalMilliseconds,
                OwnerId = user.UserId,
            }, ct);

            return result.Success
                ? Results.Ok(result)
                : Results.UnprocessableEntity(result);
        });

        return routes;
    }

    /// <summary>Replaces base64 data URLs with a short label so task history is readable.</summary>
    private static string SummarizeSource(string source)
    {
        if (source.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            // e.g. "data:application/vnd.openxmlformats-officedocument.spreadsheetml.sheet;base64,..."
            var semi = source.IndexOf(';');
            var mimeType = semi > 5 ? source[5..semi] : "file";
            var ext = mimeType switch
            {
                var m when m.Contains("spreadsheet") || m.Contains("excel") => ".xlsx",
                var m when m.Contains("csv") => ".csv",
                var m when m.Contains("json") => ".json",
                _ => ""
            };
            return $"📎 Uploaded{ext} file";
        }
        return source.Length > 80 ? source[..80] + "…" : source;
    }
}
