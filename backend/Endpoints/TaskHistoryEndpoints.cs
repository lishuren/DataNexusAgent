using DataNexus.Core;
using DataNexus.Identity;

namespace DataNexus.Endpoints;

public static class TaskHistoryEndpoints
{
    public static IEndpointRouteBuilder MapTaskHistoryEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/tasks")
            .RequireAuthorization();

        group.MapGet("/", async (
            TaskHistoryRegistry registry,
            UserContext user,
            CancellationToken ct) =>
        {
            if (!user.IsAuthenticated)
                return Results.Unauthorized();

            var tasks = await registry.GetForUserAsync(user.UserId!, 50, ct);

            return Results.Ok(tasks.Select(t => new
            {
                t.Id,
                t.Summary,
                t.AgentId,
                t.AgentName,
                t.PipelineId,
                t.PipelineName,
                t.Success,
                t.Message,
                t.RowCount,
                t.DurationMs,
                t.CreatedAt,
            }));
        });

        return routes;
    }
}
