using DataNexus.Agents;
using DataNexus.Core;
using DataNexus.Identity;
using DataNexus.Models;

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
                request.Goal,
                request.Constraints,
                request.AgentIds,
                request.ExecutionMode ?? ExecutionMode.Sequential,
                user,
                ct);

            if (plan.Steps.Count == 0)
                return Results.UnprocessableEntity("Planner could not decompose the goal into steps.");

            var name = request.Name ?? $"Plan: {Truncate(request.Goal, 80)}";

            return await RegistryExceptionResults.ExecuteAsync(async () =>
            {
                var orch = await registry.CreateAsync(
                    user.UserId,
                    name,
                    request.Goal,
                    plan.Steps,
                    plannerModel: plan.Model,
                    plannerNotes: plan.Notes,
                    enableSelfCorrection: request.EnableSelfCorrection ?? true,
                    maxCorrectionAttempts: request.MaxCorrectionAttempts ?? 3,
                    workflowKind: OrchestrationWorkflowKind.Structured,
                    graph: null,
                    executionMode: request.ExecutionMode ?? ExecutionMode.Sequential,
                    triageStepNumber: request.TriageStepNumber ?? 1,
                    groupChatMaxIterations: request.GroupChatMaxIterations ?? 10,
                    ct: ct);

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

            if (request.WorkflowKind != OrchestrationWorkflowKind.Graph && request.Steps is not { Count: >= 1 })
                return Results.BadRequest("At least one step is required.");

            return await RegistryExceptionResults.ExecuteAsync(async () =>
            {
                var orch = await registry.UpdateAsync(
                    user.UserId,
                    id,
                    request.Name,
                    request.Steps,
                    enableSelfCorrection: request.EnableSelfCorrection ?? true,
                    maxCorrectionAttempts: request.MaxCorrectionAttempts ?? 3,
                    workflowKind: request.WorkflowKind ?? OrchestrationWorkflowKind.Structured,
                    graph: request.Graph,
                    executionMode: request.ExecutionMode ?? ExecutionMode.Sequential,
                    triageStepNumber: request.TriageStepNumber ?? 1,
                    groupChatMaxIterations: request.GroupChatMaxIterations ?? 10,
                    ct: ct);

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

        group.MapPost("/{id:int}/run/stream", async (
            int id,
            RunOrchestrationRequest request,
            OrchestrationRegistry registry,
            IAgentExecutionRuntime runtime,
            TaskHistoryRegistry history,
            UserContext user,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            if (!user.IsAuthenticated)
            {
                httpContext.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            if (string.IsNullOrWhiteSpace(request.InputSource))
            {
                httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
                await httpContext.Response.WriteAsync("inputSource is required.", ct);
                return;
            }

            OrchestrationEntity entity;
            try
            {
                entity = await registry.StartRunAsync(user.UserId, id, ct);
            }
            catch (InvalidOperationException ex)
            {
                httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
                await httpContext.Response.WriteAsync(ex.Message, ct);
                return;
            }

            var orchestration = entity.ToDefinition();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            ProcessingResult? finalResult = null;

            try
            {
                finalResult = await ProcessingStreamWriter.WriteNdjsonAsync(
                    httpContext.Response,
                    runtime.StreamOrchestrationAsync(orchestration, request.InputSource, user, ct),
                    ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                finalResult = ProcessingResult.Fail($"Orchestration '{orchestration.Name}' stream was cancelled.");
            }
            finally
            {
                sw.Stop();

                if (finalResult is null)
                    finalResult = ProcessingResult.Fail($"Orchestration '{orchestration.Name}' ended without a final result.");

                var finalStatus = finalResult.Success
                    ? OrchestrationStatus.Completed
                    : OrchestrationStatus.Failed;
                await registry.CompleteRunAsync(id, finalStatus, CancellationToken.None);

                await history.RecordAsync(new TaskHistoryEntity
                {
                    Summary = $"Orchestration: {orchestration.Name}",
                    PipelineName = orchestration.Name,
                    Success = finalResult.Success,
                    Message = finalResult.Message,
                    DurationMs = sw.Elapsed.TotalMilliseconds,
                    OwnerId = user.UserId,
                }, CancellationToken.None);
            }
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
        WorkflowKind = o.WorkflowKind.ToString(),
        o.Graph,
        Status = o.Status.ToString(),
        o.PlannerModel, o.PlannerNotes,
        o.EnableSelfCorrection, o.MaxCorrectionAttempts,
        ExecutionMode = o.ExecutionMode.ToString(),
        o.TriageStepNumber, o.GroupChatMaxIterations,
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
        int? MaxCorrectionAttempts,
        ExecutionMode? ExecutionMode,
        int? TriageStepNumber,
        int? GroupChatMaxIterations);

    private sealed record UpdateOrchestrationRequest(
        string Name,
        IReadOnlyList<OrchestrationStep>? Steps,
        OrchestrationWorkflowKind? WorkflowKind,
        OrchestrationGraph? Graph,
        bool? EnableSelfCorrection,
        int? MaxCorrectionAttempts,
        ExecutionMode? ExecutionMode,
        int? TriageStepNumber,
        int? GroupChatMaxIterations);

    private sealed record RejectRequest(string? Reason);

    private sealed record RunOrchestrationRequest(string InputSource);

    private sealed record CloneOrchestrationRequest(string Name);
}
