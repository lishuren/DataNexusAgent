using DataNexus.Agents;
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
            DataNexusEngine engine,
            UserContext user,
            CancellationToken ct) =>
        {
            if (!user.IsAuthenticated)
                return Results.Unauthorized();

            var result = await engine.ProcessAsync(request, user, ct);

            return result.Success
                ? Results.Ok(result)
                : Results.UnprocessableEntity(result);
        });

        return routes;
    }
}
