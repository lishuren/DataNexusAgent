using DataNexus.Core;
using DataNexus.Identity;

namespace DataNexus.Endpoints;

public static class SkillsEndpoints
{
    public static IEndpointRouteBuilder MapSkillsEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/skills")
            .RequireAuthorization();

        // List all skills available to the authenticated user (public + private)
        group.MapGet("/", async (SkillRegistry registry, UserContext user, CancellationToken ct) =>
        {
            if (!user.IsAuthenticated)
                return Results.Unauthorized();

            var skills = await registry.GetSkillsForUserAsync(user.UserId, ct);
            return Results.Ok(skills.Select(s => new
            {
                s.Id,
                s.Name,
                s.Instructions,
                Scope = s.Scope.ToString(),
                s.OwnerId,
                s.PublishedByUserId
            }));
        });

        // List public skills only
        group.MapGet("/public", async (SkillRegistry registry, CancellationToken ct) =>
        {
            var skills = await registry.GetPublicSkillsAsync(ct);
            return Results.Ok(skills.Select(s => new
            {
                s.Id,
                s.Name,
                s.Instructions,
                Scope = s.Scope.ToString(),
                s.OwnerId,
                s.PublishedByUserId
            }));
        });

        // Get a single skill (for edit prefill)
        group.MapGet("/{id:int}", async (int id, SkillRegistry registry, CancellationToken ct) =>
        {
            var skill = await registry.GetSkillByIdAsync(id, ct);
            return skill is not null ? Results.Ok(skill) : Results.NotFound();
        });

        // Create a new private skill for the authenticated user
        group.MapPost("/", async (
            CreateSkillRequest request,
            SkillRegistry registry,
            UserContext user,
            CancellationToken ct) =>
        {
            if (!user.IsAuthenticated)
                return Results.Unauthorized();

            var skill = await registry.CreateUserSkillAsync(
                user.UserId, request.Name, request.Instructions, ct);

            return Results.Created(
                $"/api/skills/{skill.Name}",
                new { skill.Id, skill.Name, Scope = skill.Scope.ToString() });
        });

        // Update a private skill (name + instructions)
        group.MapPut("/{id:int}", async (
            int id,
            UpdateSkillRequest request,
            SkillRegistry registry,
            UserContext user,
            CancellationToken ct) =>
        {
            if (!user.IsAuthenticated)
                return Results.Unauthorized();

            if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.Instructions))
                return Results.BadRequest("Name and instructions are required.");

            var skill = await registry.UpdateSkillAsync(user.UserId, id, request.Name, request.Instructions, ct);
            return Results.Ok(new { skill.Id, skill.Name, Scope = skill.Scope.ToString() });
        });

        // Delete a private skill
        group.MapDelete("/{id:int}", async (
            int id,
            SkillRegistry registry,
            UserContext user,
            CancellationToken ct) =>
        {
            if (!user.IsAuthenticated)
                return Results.Unauthorized();

            await registry.DeleteSkillAsync(user.UserId, id, ct);
            return Results.NoContent();
        });

        // Clone a skill (public or owned private) into a new private skill
        group.MapPost("/{id:int}/clone", async (
            int id,
            CloneSkillRequest request,
            SkillRegistry registry,
            UserContext user,
            CancellationToken ct) =>
        {
            if (!user.IsAuthenticated)
                return Results.Unauthorized();

            if (string.IsNullOrWhiteSpace(request.Name))
                return Results.BadRequest("Name is required.");

            var skill = await registry.CloneSkillAsync(user.UserId, id, request.Name, ct);
            return Results.Created($"/api/skills/{skill.Id}", new { skill.Id, skill.Name, Scope = skill.Scope.ToString() });
        });

        // Publish a private skill to the public marketplace
        group.MapPost("/{id:int}/publish", async (
            int id,
            SkillRegistry registry,
            UserContext user,
            CancellationToken ct) =>
        {
            if (!user.IsAuthenticated)
                return Results.Unauthorized();

            var skill = await registry.PublishSkillAsync(user.UserId, id, ct);
            return Results.Ok(new { skill.Name, Scope = skill.Scope.ToString() });
        });

        // Unpublish a public skill back to private
        group.MapPost("/{id:int}/unpublish", async (
            int id,
            SkillRegistry registry,
            UserContext user,
            CancellationToken ct) =>
        {
            if (!user.IsAuthenticated)
                return Results.Unauthorized();

            var skill = await registry.UnpublishSkillAsync(user.UserId, id, ct);
            return Results.Ok(new { skill.Id, skill.Name, Scope = skill.Scope.ToString() });
        });

        return routes;
    }

    private sealed record CreateSkillRequest(string Name, string Instructions);

    private sealed record UpdateSkillRequest(string Name, string Instructions);

    private sealed record CloneSkillRequest(string Name);
}
