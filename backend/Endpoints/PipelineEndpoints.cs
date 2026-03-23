using DataNexus.Core;
using DataNexus.Identity;

namespace DataNexus.Endpoints;

public static class PipelineEndpoints
{
    public static IEndpointRouteBuilder MapPipelineEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/pipelines")
            .RequireAuthorization();

        // List pipelines available to the authenticated user (public + private)
        group.MapGet("/", async (PipelineRegistry registry, UserContext user, CancellationToken ct) =>
        {
            if (!user.IsAuthenticated)
                return Results.Unauthorized();

            var pipelines = await registry.GetPipelinesForUserAsync(user.UserId, ct);
            return Results.Ok(pipelines.Select(p => new
            {
                p.Id, p.Name, p.AgentIds,
                p.EnableSelfCorrection, p.MaxCorrectionAttempts,
                Scope = p.Scope.ToString(), p.OwnerId, p.PublishedByUserId
            }));
        });

        // List public pipelines (marketplace)
        group.MapGet("/public", async (PipelineRegistry registry, CancellationToken ct) =>
        {
            var pipelines = await registry.GetPublicPipelinesAsync(ct);
            return Results.Ok(pipelines.Select(p => new
            {
                p.Id, p.Name, p.AgentIds,
                p.EnableSelfCorrection, p.MaxCorrectionAttempts,
                Scope = p.Scope.ToString(), p.OwnerId, p.PublishedByUserId
            }));
        });

        // Get a single pipeline
        group.MapGet("/{id:int}", async (int id, PipelineRegistry registry, UserContext user, CancellationToken ct) =>
        {
            if (!user.IsAuthenticated)
                return Results.Unauthorized();

            var pipeline = await registry.GetByIdForUserAsync(user.UserId, id, ct);
            return pipeline is not null ? Results.Ok(pipeline) : Results.NotFound();
        });

        // Create a new private pipeline
        group.MapPost("/", async (
            CreatePipelineRequest request,
            PipelineRegistry registry,
            UserContext user,
            CancellationToken ct) =>
        {
            if (!user.IsAuthenticated)
                return Results.Unauthorized();

            if (string.IsNullOrWhiteSpace(request.Name))
                return Results.BadRequest("Pipeline name is required.");

            if (request.AgentIds is not { Count: >= 2 })
                return Results.BadRequest("A pipeline must have at least 2 agent steps.");

            return await RegistryExceptionResults.ExecuteAsync(async () =>
            {
                var pipeline = await registry.CreateAsync(
                    user.UserId, request.Name, request.AgentIds,
                    request.EnableSelfCorrection ?? true,
                    request.MaxCorrectionAttempts ?? 3, ct);

                return Results.Created($"/api/pipelines/{pipeline.Id}", new
                {
                    pipeline.Id, pipeline.Name,
                    Scope = pipeline.Scope.ToString()
                });
            });
        });

        // Update an existing pipeline
        group.MapPut("/{id:int}", async (
            int id,
            UpdatePipelineRequest request,
            PipelineRegistry registry,
            UserContext user,
            CancellationToken ct) =>
        {
            if (!user.IsAuthenticated)
                return Results.Unauthorized();

            if (string.IsNullOrWhiteSpace(request.Name))
                return Results.BadRequest("Pipeline name is required.");

            if (request.AgentIds is not { Count: >= 2 })
                return Results.BadRequest("A pipeline must have at least 2 agent steps.");

            return await RegistryExceptionResults.ExecuteAsync(async () =>
            {
                var pipeline = await registry.UpdateAsync(
                    user.UserId, id, request.Name, request.AgentIds,
                    request.EnableSelfCorrection ?? true,
                    request.MaxCorrectionAttempts ?? 3, ct);

                return Results.Ok(new
                {
                    pipeline.Id, pipeline.Name,
                    Scope = pipeline.Scope.ToString()
                });
            });
        });

        // Delete a pipeline
        group.MapDelete("/{id:int}", async (
            int id,
            PipelineRegistry registry,
            UserContext user,
            CancellationToken ct) =>
        {
            if (!user.IsAuthenticated)
                return Results.Unauthorized();

            return await RegistryExceptionResults.ExecuteAsync(async () =>
            {
                await registry.DeleteAsync(user.UserId, id, ct);
                return Results.NoContent();
            });
        });

        // Publish a private pipeline to public
        group.MapPost("/{id:int}/publish", async (
            int id,
            PipelineRegistry registry,
            UserContext user,
            CancellationToken ct) =>
        {
            if (!user.IsAuthenticated)
                return Results.Unauthorized();

            return await RegistryExceptionResults.ExecuteAsync(async () =>
            {
                var pipeline = await registry.PublishAsync(user.UserId, id, ct);
                return Results.Ok(new { pipeline.Id, pipeline.Name, Scope = pipeline.Scope.ToString() });
            });
        });

        // Unpublish a public pipeline back to private
        group.MapPost("/{id:int}/unpublish", async (
            int id,
            PipelineRegistry registry,
            UserContext user,
            CancellationToken ct) =>
        {
            if (!user.IsAuthenticated)
                return Results.Unauthorized();

            return await RegistryExceptionResults.ExecuteAsync(async () =>
            {
                var pipeline = await registry.UnpublishAsync(user.UserId, id, ct);
                return Results.Ok(new { pipeline.Id, pipeline.Name, Scope = pipeline.Scope.ToString() });
            });
        });

        // Clone a pipeline (public or owned private) into a new private pipeline
        group.MapPost("/{id:int}/clone", async (
            int id,
            ClonePipelineRequest request,
            PipelineRegistry registry,
            UserContext user,
            CancellationToken ct) =>
        {
            if (!user.IsAuthenticated)
                return Results.Unauthorized();

            if (string.IsNullOrWhiteSpace(request.Name))
                return Results.BadRequest("Pipeline name is required.");

            return await RegistryExceptionResults.ExecuteAsync(async () =>
            {
                var pipeline = await registry.CloneAsync(user.UserId, id, request.Name, ct);
                return Results.Created($"/api/pipelines/{pipeline.Id}", new
                {
                    pipeline.Id, pipeline.Name,
                    Scope = pipeline.Scope.ToString()
                });
            });
        });

        return routes;
    }

    private sealed record CreatePipelineRequest(
        string Name,
        IReadOnlyList<int> AgentIds,
        bool? EnableSelfCorrection,
        int? MaxCorrectionAttempts);

    private sealed record UpdatePipelineRequest(
        string Name,
        IReadOnlyList<int> AgentIds,
        bool? EnableSelfCorrection,
        int? MaxCorrectionAttempts);

    private sealed record ClonePipelineRequest(string Name);
}
