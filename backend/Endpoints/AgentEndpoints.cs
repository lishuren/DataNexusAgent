using DataNexus.Agents;
using DataNexus.Core;
using DataNexus.Identity;
using Microsoft.Extensions.Options;

namespace DataNexus.Endpoints;

public static class AgentEndpoints
{
    public static IEndpointRouteBuilder MapAgentEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/agents")
            .RequireAuthorization();

        // List agents available to the authenticated user (public + private)
        group.MapGet("/", async (AgentRegistry registry, UserContext user, CancellationToken ct) =>
        {
            if (!user.IsAuthenticated)
                return Results.Unauthorized();

            var agents = await registry.GetAgentsForUserAsync(user.UserId, ct);
            return Results.Ok(agents.Select(a => new
            {
                a.Id, a.Name, a.Icon, a.Description,
                ExecutionType = a.ExecutionType.ToString(),
                a.Command, a.Arguments, a.WorkingDirectory, a.TimeoutSeconds,
                a.UiSchema, a.Plugins, a.Skills,
                Scope = a.Scope.ToString(), a.OwnerId, a.PublishedByUserId, a.IsBuiltIn
            }));
        });

        // List public agents (marketplace)
        group.MapGet("/public", async (AgentRegistry registry, CancellationToken ct) =>
        {
            var agents = await registry.GetPublicAgentsAsync(ct);
            return Results.Ok(agents.Select(a => new
            {
                a.Id, a.Name, a.Icon, a.Description,
                ExecutionType = a.ExecutionType.ToString(),
                a.Command, a.TimeoutSeconds,
                a.UiSchema, a.Plugins, a.Skills,
                Scope = a.Scope.ToString(), a.OwnerId, a.PublishedByUserId, a.IsBuiltIn
            }));
        });

        // Get a single agent (including UI schema for rendering)
        group.MapGet("/{id:int}", async (int id, AgentRegistry registry, UserContext user, CancellationToken ct) =>
        {
            if (!user.IsAuthenticated)
                return Results.Unauthorized();

            var agent = await registry.GetAgentByIdForUserAsync(user.UserId, id, ct);
            if (agent is null) return Results.NotFound();

            return Results.Ok(new
            {
                agent.Id, agent.Name, agent.Icon, agent.Description,
                ExecutionType = agent.ExecutionType.ToString(),
                SystemPrompt = agent.SystemPrompt,
                agent.Command, agent.Arguments, agent.WorkingDirectory, agent.TimeoutSeconds,
                agent.UiSchema, agent.Plugins, agent.Skills,
                Scope = agent.Scope.ToString(), agent.OwnerId, agent.PublishedByUserId, agent.IsBuiltIn
            });
        });

        // Create a new private agent
        group.MapPost("/", async (
            CreateAgentRequest request,
            AgentRegistry registry,
            IOptions<ExternalAgentOptions> extOpts,
            UserContext user,
            CancellationToken ct) =>
        {
            if (!user.IsAuthenticated)
                return Results.Unauthorized();

            // Parse execution type (default LLM)
            if (!Enum.TryParse<AgentExecutionType>(request.ExecutionType ?? "Llm", true, out var execType))
                return Results.BadRequest("Invalid executionType. Use 'Llm' or 'External'.");

            // Validate external agent requirements
            if (execType == AgentExecutionType.External)
            {
                if (string.IsNullOrWhiteSpace(request.Command))
                    return Results.BadRequest("External agents require a 'command'.");

                if (!extOpts.Value.Enabled)
                    return Results.BadRequest("External agent execution is disabled by server configuration.");

                var cmdName = Path.GetFileName(request.Command);
                var allowed = extOpts.Value.AllowedCommands.Exists(a =>
                    string.Equals(a, request.Command, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(a, cmdName, StringComparison.OrdinalIgnoreCase));
                if (!allowed)
                    return Results.BadRequest($"Command '{request.Command}' is not in the server allowlist.");
            }

            var timeout = Math.Clamp(request.TimeoutSeconds ?? 30, 1, extOpts.Value.MaxTimeoutSeconds);

            return await RegistryExceptionResults.ExecuteAsync(async () =>
            {
                var agent = await registry.CreateAgentAsync(
                    user.UserId, request.Name, request.Icon, request.Description,
                    request.SystemPrompt ?? string.Empty, request.UiSchema,
                    request.Plugins ?? string.Empty, request.Skills ?? string.Empty,
                    execType, request.Command, request.Arguments,
                    request.WorkingDirectory, timeout, ct);

                return Results.Created($"/api/agents/{agent.Id}", new
                {
                    agent.Id, agent.Name,
                    ExecutionType = agent.ExecutionType.ToString(),
                    Scope = agent.Scope.ToString()
                });
            });
        });

        // Publish a private agent to public marketplace
        group.MapPost("/{id:int}/publish", async (
            int id,
            AgentRegistry registry,
            UserContext user,
            CancellationToken ct) =>
        {
            if (!user.IsAuthenticated)
                return Results.Unauthorized();

            return await RegistryExceptionResults.ExecuteAsync(async () =>
            {
                var agent = await registry.PublishAgentAsync(user.UserId, id, ct);
                return Results.Ok(new { agent.Id, agent.Name, Scope = agent.Scope.ToString() });
            });
        });

        // Unpublish a public agent back to private
        group.MapPost("/{id:int}/unpublish", async (
            int id,
            AgentRegistry registry,
            UserContext user,
            CancellationToken ct) =>
        {
            if (!user.IsAuthenticated)
                return Results.Unauthorized();

            return await RegistryExceptionResults.ExecuteAsync(async () =>
            {
                var agent = await registry.UnpublishAgentAsync(user.UserId, id, ct);
                return Results.Ok(new { agent.Id, agent.Name, Scope = agent.Scope.ToString() });
            });
        });

        // Update an existing private agent
        group.MapPut("/{id:int}", async (
            int id,
            UpdateAgentRequest request,
            AgentRegistry registry,
            IOptions<ExternalAgentOptions> extOpts,
            UserContext user,
            CancellationToken ct) =>
        {
            if (!user.IsAuthenticated)
                return Results.Unauthorized();

            if (!Enum.TryParse<AgentExecutionType>(request.ExecutionType ?? "Llm", true, out var execType))
                return Results.BadRequest("Invalid executionType. Use 'Llm' or 'External'.");

            if (execType == AgentExecutionType.External)
            {
                if (string.IsNullOrWhiteSpace(request.Command))
                    return Results.BadRequest("External agents require a 'command'.");

                if (!extOpts.Value.Enabled)
                    return Results.BadRequest("External agent execution is disabled by server configuration.");

                var cmdName = Path.GetFileName(request.Command);
                var allowed = extOpts.Value.AllowedCommands.Exists(a =>
                    string.Equals(a, request.Command, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(a, cmdName, StringComparison.OrdinalIgnoreCase));
                if (!allowed)
                    return Results.BadRequest($"Command '{request.Command}' is not in the server allowlist.");
            }

            var timeout = Math.Clamp(request.TimeoutSeconds ?? 30, 1, extOpts.Value.MaxTimeoutSeconds);

            return await RegistryExceptionResults.ExecuteAsync(async () =>
            {
                var agent = await registry.UpdateAgentAsync(
                    user.UserId, id, request.Name, request.Icon, request.Description,
                    request.SystemPrompt ?? string.Empty, request.UiSchema,
                    request.Plugins ?? string.Empty, request.Skills ?? string.Empty,
                    execType, request.Command, request.Arguments,
                    request.WorkingDirectory, timeout, ct);

                return Results.Ok(new
                {
                    agent.Id, agent.Name,
                    ExecutionType = agent.ExecutionType.ToString(),
                    Scope = agent.Scope.ToString()
                });
            });
        });

        // Delete a private agent
        group.MapDelete("/{id:int}", async (
            int id,
            AgentRegistry registry,
            UserContext user,
            CancellationToken ct) =>
        {
            if (!user.IsAuthenticated)
                return Results.Unauthorized();

            return await RegistryExceptionResults.ExecuteAsync(async () =>
            {
                await registry.DeleteAgentAsync(user.UserId, id, ct);
                return Results.NoContent();
            });
        });

        // Clone an agent (public or owned private) into a new private agent
        group.MapPost("/{id:int}/clone", async (
            int id,
            CloneAgentRequest request,
            AgentRegistry registry,
            UserContext user,
            CancellationToken ct) =>
        {
            if (!user.IsAuthenticated)
                return Results.Unauthorized();

            if (string.IsNullOrWhiteSpace(request.Name))
                return Results.BadRequest("Name is required.");

            return await RegistryExceptionResults.ExecuteAsync(async () =>
            {
                var agent = await registry.CloneAgentAsync(user.UserId, id, request.Name, ct);
                return Results.Created($"/api/agents/{agent.Id}", new
                {
                    agent.Id, agent.Name,
                    ExecutionType = agent.ExecutionType.ToString(),
                    Scope = agent.Scope.ToString()
                });
            });
        });

        return routes;
    }

    private sealed record CreateAgentRequest(
        string Name,
        string Icon,
        string Description,
        string? SystemPrompt,
        string? UiSchema,
        string? Plugins,
        string? Skills,
        string? ExecutionType,
        string? Command,
        string? Arguments,
        string? WorkingDirectory,
        int? TimeoutSeconds);

    private sealed record UpdateAgentRequest(
        string Name,
        string Icon,
        string Description,
        string? SystemPrompt,
        string? UiSchema,
        string? Plugins,
        string? Skills,
        string? ExecutionType,
        string? Command,
        string? Arguments,
        string? WorkingDirectory,
        int? TimeoutSeconds);

    private sealed record CloneAgentRequest(string Name);
}
