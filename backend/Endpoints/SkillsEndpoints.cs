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
                s.Name,
                Scope = s.Scope.ToString(),
                s.OwnerId
            }));
        });

        // List public skills only
        group.MapGet("/public", async (SkillRegistry registry, CancellationToken ct) =>
        {
            var skills = await registry.GetPublicSkillsAsync(ct);
            return Results.Ok(skills.Select(s => new { s.Name, s.Instructions }));
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
                new { skill.Name, Scope = skill.Scope.ToString() });
        });

        // Publish a private skill to the public marketplace
        group.MapPost("/{name}/publish", async (
            string name,
            SkillRegistry registry,
            UserContext user,
            CancellationToken ct) =>
        {
            if (!user.IsAuthenticated)
                return Results.Unauthorized();

            var skill = await registry.PublishSkillAsync(user.UserId, name, ct);
            return Results.Ok(new { skill.Name, Scope = skill.Scope.ToString() });
        });

        return routes;
    }

    private sealed record CreateSkillRequest(string Name, string Instructions);
}
