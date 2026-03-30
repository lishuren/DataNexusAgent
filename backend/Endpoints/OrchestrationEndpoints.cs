using DataNexus.Agents;
using DataNexus.Core;
using DataNexus.Identity;

namespace DataNexus.Endpoints;

public static class OrchestrationEndpoints
{
    public static IEndpointRouteBuilder MapOrchestrationEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/orchestrations")
            .RequireAuthorization();

        // ── Plan: LLM decomposes goal into steps ─────────────────────────

        group.MapPost("/plan", async (
            PlanRequest request,
            PlannerService planner,
            OrchestrationRegistry registry,
            UserContext user,
            CancellationToken ct) =>
        {
            if (!user.IsAuthenticated)
                return Results.Unauthorized();

            if (string.IsNullOrWhiteSpace(request.Goal))
                return Results.BadRequest("A goal is required.");

            var plan = await planner.GeneratePlanAsync(
                request.Goal, request.Constraints, request.AgentIds, user, ct);

            if (plan.Steps.Count == 0)
                return Results.UnprocessableEntity("Planner could not decompose the goal into steps.");

            var name = request.Name ?? $"Plan: {Truncate(request.Goal, 80)}";

            return await RegistryExceptionResults.ExecuteAsync(async () =>
            {
                var orch = await registry.CreateAsync(
                    user.UserId, name, request.Goal, plan.Steps,
                    plan.Model, plan.Notes,
                    request.EnableSelfCorrection ?? true,
                    request.MaxCorrectionAttempts ?? 3, ct);

                return Results.Created($"/api/orchestrations/{orch.Id}", ToResponse(orch));
            });
        });

        // ── List (user's own + public) ───────────────────────────────────

        group.MapGet("/", async (
            OrchestrationRegistry registry,
            UserContext user,
            CancellationToken ct) =>
        {
            if (!user.IsAuthenticated)
                return Results.Unauthorized();

            var items = await registry.GetForUserAsync(user.UserId, ct);
            return Results.Ok(items.Select(ToResponse));
        });

        // ── List public (marketplace) ────────────────────────────────────

        group.MapGet("/public", async (
            OrchestrationRegistry registry,
            CancellationToken ct) =>
        {
            var items = await registry.GetPublicAsync(ct);
            return Results.Ok(items.Select(ToResponse));
        });

        // ── Get single ──────────────────────────────────────────────────

        group.MapGet("/{id:int}", async (
            int id,
            OrchestrationRegistry registry,
            UserContext user,
            CancellationToken ct) =>
        {
            if (!user.IsAuthenticated)
                return Results.Unauthorized();

            var orch = await registry.GetByIdForUserAsync(user.UserId, id, ct);
            return orch is not null ? Results.Ok(ToResponse(orch)) : Results.NotFound();
        });

        // ── Update draft ─────────────────────────────────────────────────

        group.MapPut("/{id:int}", async (
            int id,
            UpdateOrchestrationRequest request,
            OrchestrationRegistry registry,
            UserContext user,
            CancellationToken ct) =>
        {
            if (!user.IsAuthenticated)
                return Results.Unauthorized();

            if (string.IsNullOrWhiteSpace(request.Name))
                return Results.BadRequest("Name is required.");

            if (request.Steps is not { Count: >= 1 })
                return Results.BadRequest("At least one step is required.");

            return await RegistryExceptionResults.ExecuteAsync(async () =>
            {
                var orch = await registry.UpdateAsync(
                    user.UserId, id, request.Name, request.Steps,
                    request.EnableSelfCorrection ?? true,
                    request.MaxCorrectionAttempts ?? 3, ct);

                return Results.Ok(ToResponse(orch));
            });
        });

        // ── Approve ──────────────────────────────────────────────────────

        group.MapPost("/{id:int}/approve", async (
            int id,
            OrchestrationRegistry registry,
            UserContext user,
            CancellationToken ct) =>
        {
            if (!user.IsAuthenticated)
                return Results.Unauthorized();

            return await RegistryExceptionResults.ExecuteAsync(async () =>
            {
                var orch = await registry.ApproveAsync(user.UserId, id, ct);
                return Results.Ok(ToResponse(orch));
            });
        });

        // ── Reject ───────────────────────────────────────────────────────

        group.MapPost("/{id:int}/reject", async (
            int id,
            RejectRequest? request,
            OrchestrationRegistry registry,
            UserContext user,
            CancellationToken ct) =>
        {
            if (!user.IsAuthenticated)
                return Results.Unauthorized();

            return await RegistryExceptionResults.ExecuteAsync(async () =>
            {
                var orch = await registry.RejectAsync(user.UserId, id, request?.Reason, ct);
                return Results.Ok(ToResponse(orch));
            });
        });

        // ── Reset to Draft ───────────────────────────────────────────────

        group.MapPost("/{id:int}/reset", async (
            int id,
            OrchestrationRegistry registry,
            UserContext user,
            CancellationToken ct) =>
        {
            if (!user.IsAuthenticated)
                return Results.Unauthorized();

            return await RegistryExceptionResults.ExecuteAsync(async () =>
            {
                var orch = await registry.ResetToDraftAsync(user.UserId, id, ct);
                return Results.Ok(ToResponse(orch));
            });
        });

        // ── Run (approved only) ──────────────────────────────────────────

        group.MapPost("/{id:int}/run", async (
            int id,
            RunOrchestrationRequest request,
            OrchestrationRegistry registry,
            IAgentExecutionRuntime runtime,
            TaskHistoryRegistry history,
            UserContext user,
            CancellationToken ct) =>
        {
            if (!user.IsAuthenticated)
                return Results.Unauthorized();

            if (string.IsNullOrWhiteSpace(request.InputSource))
                return Results.BadRequest("inputSource is required.");

            // StartRunAsync validates Approved status
            OrchestrationEntity entity;
            try
            {
                entity = await registry.StartRunAsync(user.UserId, id, ct);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(ex.Message);
            }

            var orch = entity.ToDefinition();

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var result = await runtime.RunOrchestrationAsync(orch, request.InputSource, user, ct);
            sw.Stop();

            // Update final status
            var finalStatus = result.Success
                ? OrchestrationStatus.Completed
                : OrchestrationStatus.Failed;
            await registry.CompleteRunAsync(id, finalStatus, ct);

            // Record in task history
            await history.RecordAsync(new TaskHistoryEntity
            {
                Summary = $"Orchestration: {orch.Name}",
                PipelineName = orch.Name,
                Success = result.Success,
                Message = result.Message,
                DurationMs = sw.Elapsed.TotalMilliseconds,
                OwnerId = user.UserId,
            }, ct);

            return result.Success
                ? Results.Ok(result)
                : Results.UnprocessableEntity(result);
        });

        // ── Publish / Unpublish ──────────────────────────────────────────

        group.MapPost("/{id:int}/publish", async (
            int id,
            OrchestrationRegistry registry,
            UserContext user,
            CancellationToken ct) =>
        {
            if (!user.IsAuthenticated)
                return Results.Unauthorized();

            return await RegistryExceptionResults.ExecuteAsync(async () =>
            {
                var orch = await registry.PublishAsync(user.UserId, id, ct);
                return Results.Ok(ToResponse(orch));
            });
        });

        group.MapPost("/{id:int}/unpublish", async (
            int id,
            OrchestrationRegistry registry,
            UserContext user,
            CancellationToken ct) =>
        {
            if (!user.IsAuthenticated)
                return Results.Unauthorized();

            return await RegistryExceptionResults.ExecuteAsync(async () =>
            {
                var orch = await registry.UnpublishAsync(user.UserId, id, ct);
                return Results.Ok(ToResponse(orch));
            });
        });

        // ── Clone ────────────────────────────────────────────────────────

        group.MapPost("/{id:int}/clone", async (
            int id,
            CloneOrchestrationRequest request,
            OrchestrationRegistry registry,
            UserContext user,
            CancellationToken ct) =>
        {
            if (!user.IsAuthenticated)
                return Results.Unauthorized();

            if (string.IsNullOrWhiteSpace(request.Name))
                return Results.BadRequest("Name is required.");

            return await RegistryExceptionResults.ExecuteAsync(async () =>
            {
                var orch = await registry.CloneAsync(user.UserId, id, request.Name, ct);
                return Results.Created($"/api/orchestrations/{orch.Id}", ToResponse(orch));
            });
        });

        // ── Delete ───────────────────────────────────────────────────────

        group.MapDelete("/{id:int}", async (
            int id,
            OrchestrationRegistry registry,
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

        return routes;
    }

    // ── Response shape ───────────────────────────────────────────────────

    private static object ToResponse(OrchestrationDefinition o) => new
    {
        o.Id, o.Name, o.Goal, o.Steps,
        Status = o.Status.ToString(),
        o.PlannerModel, o.PlannerNotes,
        o.EnableSelfCorrection, o.MaxCorrectionAttempts,
        Scope = o.Scope.ToString(),
        o.OwnerId, o.PublishedByUserId,
        o.ApprovedAt, o.CreatedAt, o.UpdatedAt,
    };

    private static string Truncate(string s, int max) =>
        s.Length > max ? s[..max] + "…" : s;

    // ── Request records ──────────────────────────────────────────────────

    private sealed record PlanRequest(
        string Goal,
        string? Name,
        string? Constraints,
        IReadOnlyList<int>? AgentIds,
        bool? EnableSelfCorrection,
        int? MaxCorrectionAttempts);

    private sealed record UpdateOrchestrationRequest(
        string Name,
        IReadOnlyList<OrchestrationStep> Steps,
        bool? EnableSelfCorrection,
        int? MaxCorrectionAttempts);

    private sealed record RejectRequest(string? Reason);

    private sealed record RunOrchestrationRequest(string InputSource);

    private sealed record CloneOrchestrationRequest(string Name);
}
