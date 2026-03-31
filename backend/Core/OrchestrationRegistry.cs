using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace DataNexus.Core;

public sealed class OrchestrationRegistry(
    IServiceScopeFactory scopeFactory,
    ILogger<OrchestrationRegistry> logger)
{
    // ── Queries ──────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<OrchestrationDefinition>> GetForUserAsync(
        string userId, CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DataNexusDbContext>();

        var entities = await db.Orchestrations
            .Where(o => o.Scope == SkillScope.Public || o.OwnerId == userId)
            .AsNoTracking()
            .OrderByDescending(o => o.UpdatedAt)
            .ToListAsync(ct);

        return [.. entities.Select(e => e.ToDefinition())];
    }

    public async Task<IReadOnlyList<OrchestrationDefinition>> GetPublicAsync(
        CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DataNexusDbContext>();

        var entities = await db.Orchestrations
            .Where(o => o.Scope == SkillScope.Public)
            .AsNoTracking()
            .OrderByDescending(o => o.UpdatedAt)
            .ToListAsync(ct);

        return [.. entities.Select(e => e.ToDefinition())];
    }

    public async Task<OrchestrationDefinition?> GetByIdAsync(
        int id, CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DataNexusDbContext>();

        var entity = await db.Orchestrations.AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == id, ct);
        return entity?.ToDefinition();
    }

    public async Task<OrchestrationDefinition?> GetByIdForUserAsync(
        string userId, int id, CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DataNexusDbContext>();

        var entity = await db.Orchestrations.AsNoTracking()
            .FirstOrDefaultAsync(
                o => o.Id == id && (o.Scope == SkillScope.Public || o.OwnerId == userId), ct);
        return entity?.ToDefinition();
    }

    // ── Create (from planner output) ─────────────────────────────────────

    public async Task<OrchestrationDefinition> CreateAsync(
        string userId,
        string name,
        string goal,
        IReadOnlyList<OrchestrationStep>? steps,
        string? plannerModel = null,
        string? plannerNotes = null,
        bool enableSelfCorrection = true,
        int maxCorrectionAttempts = 3,
        OrchestrationWorkflowKind workflowKind = OrchestrationWorkflowKind.Structured,
        OrchestrationGraph? graph = null,
        ExecutionMode executionMode = ExecutionMode.Sequential,
        int triageStepNumber = 1,
        int groupChatMaxIterations = 10,
        CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DataNexusDbContext>();

        await EnsureNameAvailableAsync(db, userId, name, null, ct);

        var (storedSteps, storedGraph) = NormalizeWorkflowData(steps, workflowKind, graph);
        var agentNames = await GetAccessibleAgentNamesAsync(
            db, userId, GetReferencedAgentIds(storedSteps, storedGraph), ct);
        storedSteps = ApplyAgentNames(storedSteps, agentNames);
        storedGraph = storedGraph is null ? null : ApplyAgentNames(storedGraph, agentNames);

        var entity = new OrchestrationEntity
        {
            Name = name,
            Goal = goal,
            StepsJson = JsonSerializer.Serialize(storedSteps),
            WorkflowKind = workflowKind,
            GraphJson = storedGraph is null ? null : JsonSerializer.Serialize(storedGraph),
            Status = OrchestrationStatus.Draft,
            PlannerModel = plannerModel,
            PlannerNotes = plannerNotes,
            EnableSelfCorrection = enableSelfCorrection,
            MaxCorrectionAttempts = Math.Clamp(maxCorrectionAttempts, 1, 10),
            ExecutionMode = executionMode,
            TriageStepNumber = Math.Clamp(triageStepNumber, 1, Math.Max(1, storedSteps.Count)),
            GroupChatMaxIterations = Math.Clamp(groupChatMaxIterations, 2, 50),
            Scope = SkillScope.Private,
            OwnerId = userId,
        };

        db.Orchestrations.Add(entity);
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "[User: {UserId}] Created orchestration '{Name}' ({Kind}, {Mode}, {StepCount} steps)",
            userId, name, workflowKind, executionMode, storedSteps.Count);

        return entity.ToDefinition();
    }

    // ── Update (draft only) ──────────────────────────────────────────────

    public async Task<OrchestrationDefinition> UpdateAsync(
        string userId,
        int id,
        string name,
        IReadOnlyList<OrchestrationStep>? steps,
        bool enableSelfCorrection,
        int maxCorrectionAttempts,
        OrchestrationWorkflowKind workflowKind = OrchestrationWorkflowKind.Structured,
        OrchestrationGraph? graph = null,
        ExecutionMode executionMode = ExecutionMode.Sequential,
        int triageStepNumber = 1,
        int groupChatMaxIterations = 10,
        CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DataNexusDbContext>();

        var entity = await GetOwnedEntityAsync(db, userId, id, ct);

        if (entity.Status != OrchestrationStatus.Draft)
            throw new InvalidOperationException(
                $"Orchestration {id} is '{entity.Status}' — only Draft orchestrations can be edited.");

        await EnsureNameAvailableAsync(db, userId, name, id, ct);

        var (storedSteps, storedGraph) = NormalizeWorkflowData(steps, workflowKind, graph);
        var agentNames = await GetAccessibleAgentNamesAsync(
            db, userId, GetReferencedAgentIds(storedSteps, storedGraph), ct);
        storedSteps = ApplyAgentNames(storedSteps, agentNames);
        storedGraph = storedGraph is null ? null : ApplyAgentNames(storedGraph, agentNames);

        entity.Name = name;
        entity.StepsJson = JsonSerializer.Serialize(storedSteps);
        entity.WorkflowKind = workflowKind;
        entity.GraphJson = storedGraph is null ? null : JsonSerializer.Serialize(storedGraph);
        entity.EnableSelfCorrection = enableSelfCorrection;
        entity.MaxCorrectionAttempts = Math.Clamp(maxCorrectionAttempts, 1, 10);
        entity.ExecutionMode = executionMode;
        entity.TriageStepNumber = Math.Clamp(triageStepNumber, 1, Math.Max(1, storedSteps.Count));
        entity.GroupChatMaxIterations = Math.Clamp(groupChatMaxIterations, 2, 50);
        entity.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "[User: {UserId}] Updated orchestration '{Name}'", userId, name);

        return entity.ToDefinition();
    }

    // ── Status transitions ───────────────────────────────────────────────

    public async Task<OrchestrationDefinition> ApproveAsync(
        string userId, int id, CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DataNexusDbContext>();

        var entity = await GetOwnedEntityAsync(db, userId, id, ct);
        EnsureStatus(entity, OrchestrationStatus.Draft, "approve");

        entity.Status = OrchestrationStatus.Approved;
        entity.ApprovedAt = DateTime.UtcNow;
        entity.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "[User: {UserId}] Approved orchestration '{Name}'", userId, entity.Name);

        return entity.ToDefinition();
    }

    public async Task<OrchestrationDefinition> RejectAsync(
        string userId, int id, string? reason = null, CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DataNexusDbContext>();

        var entity = await GetOwnedEntityAsync(db, userId, id, ct);
        EnsureStatus(entity, OrchestrationStatus.Draft, "reject");

        entity.Status = OrchestrationStatus.Rejected;
        entity.PlannerNotes = reason ?? entity.PlannerNotes;
        entity.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "[User: {UserId}] Rejected orchestration '{Name}'", userId, entity.Name);

        return entity.ToDefinition();
    }

    public async Task<OrchestrationEntity> StartRunAsync(
        string userId, int id, CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DataNexusDbContext>();

        var entity = await GetOwnedEntityAsync(db, userId, id, ct);
        EnsureStatus(entity, OrchestrationStatus.Approved, "run");

        entity.Status = OrchestrationStatus.Running;
        entity.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(ct);
        return entity;
    }

    public async Task CompleteRunAsync(
        int id, OrchestrationStatus finalStatus, CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DataNexusDbContext>();

        var entity = await db.Orchestrations.FirstOrDefaultAsync(o => o.Id == id, ct)
            ?? throw new ResourceNotFoundException($"Orchestration {id} not found.");

        entity.Status = finalStatus;
        entity.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(ct);
    }

    /// <summary>Reset an orchestration back to Draft so the user can edit and re-approve.</summary>
    public async Task<OrchestrationDefinition> ResetToDraftAsync(
        string userId, int id, CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DataNexusDbContext>();

        var entity = await GetOwnedEntityAsync(db, userId, id, ct);

        if (entity.Status is OrchestrationStatus.Running)
            throw new InvalidOperationException("Cannot reset a running orchestration.");

        entity.Status = OrchestrationStatus.Draft;
        entity.ApprovedAt = null;
        entity.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "[User: {UserId}] Reset orchestration '{Name}' to Draft", userId, entity.Name);

        return entity.ToDefinition();
    }

    // ── Publish / Unpublish / Clone / Delete ─────────────────────────────

    public async Task<OrchestrationDefinition> PublishAsync(
        string userId, int id, CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DataNexusDbContext>();

        var entity = await GetOwnedEntityAsync(db, userId, id, ct);

        if (entity.Status != OrchestrationStatus.Approved)
            throw new InvalidOperationException("Only Approved orchestrations can be published.");

        entity.Scope = SkillScope.Public;
        entity.PublishedByUserId = userId;
        entity.OwnerId = null;
        entity.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "[User: {UserId}] Published orchestration '{Name}'", userId, entity.Name);

        return entity.ToDefinition();
    }

    public async Task<OrchestrationDefinition> UnpublishAsync(
        string userId, int id, CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DataNexusDbContext>();

        var entity = await db.Orchestrations.FirstOrDefaultAsync(
            o => o.Id == id && o.Scope == SkillScope.Public, ct)
            ?? throw new ResourceNotFoundException($"Orchestration {id} not found.");

        if (!string.Equals(entity.PublishedByUserId, userId, StringComparison.Ordinal))
            throw new ResourceAccessDeniedException("You cannot unpublish this orchestration.");

        await EnsureNameAvailableAsync(db, userId, entity.Name, entity.Id, ct);

        entity.Scope = SkillScope.Private;
        entity.OwnerId = userId;
        entity.PublishedByUserId = null;
        entity.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "[User: {UserId}] Unpublished orchestration '{Name}'", userId, entity.Name);

        return entity.ToDefinition();
    }

    public async Task<OrchestrationDefinition> CloneAsync(
        string userId, int id, string newName, CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DataNexusDbContext>();

        var source = await db.Orchestrations.AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == id, ct)
            ?? throw new ResourceNotFoundException($"Orchestration {id} not found.");

        if (source.Scope != SkillScope.Public &&
            !string.Equals(source.OwnerId, userId, StringComparison.Ordinal))
            throw new ResourceNotFoundException($"Orchestration {id} not found.");

        await EnsureNameAvailableAsync(db, userId, newName, null, ct);

        var entity = new OrchestrationEntity
        {
            Name = newName,
            Goal = source.Goal,
            StepsJson = source.StepsJson,
            WorkflowKind = source.WorkflowKind,
            GraphJson = source.GraphJson,
            Status = OrchestrationStatus.Draft,
            PlannerModel = source.PlannerModel,
            PlannerNotes = source.PlannerNotes,
            EnableSelfCorrection = source.EnableSelfCorrection,
            MaxCorrectionAttempts = source.MaxCorrectionAttempts,
            ExecutionMode = source.ExecutionMode,
            TriageStepNumber = source.TriageStepNumber,
            GroupChatMaxIterations = source.GroupChatMaxIterations,
            Scope = SkillScope.Private,
            OwnerId = userId,
        };

        db.Orchestrations.Add(entity);
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "[User: {UserId}] Cloned orchestration '{Source}' as '{Clone}'",
            userId, source.Name, newName);

        return entity.ToDefinition();
    }

    public async Task DeleteAsync(
        string userId, int id, CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DataNexusDbContext>();

        var entity = await GetOwnedEntityAsync(db, userId, id, ct);

        if (entity.Status == OrchestrationStatus.Running)
            throw new InvalidOperationException("Cannot delete a running orchestration.");

        db.Orchestrations.Remove(entity);
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "[User: {UserId}] Deleted orchestration '{Name}'", userId, entity.Name);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static async Task<OrchestrationEntity> GetOwnedEntityAsync(
        DataNexusDbContext db, string userId, int id, CancellationToken ct)
    {
        return await db.Orchestrations.FirstOrDefaultAsync(
            o => o.Id == id && o.OwnerId == userId, ct)
            ?? throw new ResourceNotFoundException($"Orchestration {id} not found.");
    }

    private static void EnsureStatus(
        OrchestrationEntity entity, OrchestrationStatus required, string action)
    {
        if (entity.Status != required)
            throw new InvalidOperationException(
                $"Cannot {action} orchestration '{entity.Name}' — current status is '{entity.Status}', expected '{required}'.");
    }

    private static async Task EnsureNameAvailableAsync(
        DataNexusDbContext db, string userId, string name, int? excludeId, CancellationToken ct)
    {
        var exists = await db.Orchestrations.AnyAsync(
            o => o.OwnerId == userId && o.Name == name &&
                 (!excludeId.HasValue || o.Id != excludeId.Value), ct);

        if (exists)
            throw new ResourceConflictException($"Orchestration name '{name}' already exists.");
    }

    private static (IReadOnlyList<OrchestrationStep> Steps, OrchestrationGraph? Graph) NormalizeWorkflowData(
        IReadOnlyList<OrchestrationStep>? steps,
        OrchestrationWorkflowKind workflowKind,
        OrchestrationGraph? graph)
    {
        if (workflowKind == OrchestrationWorkflowKind.Graph)
        {
            var normalizedGraph = OrchestrationGraphRules.NormalizeGraph(graph);
            return (normalizedGraph.ToStructuredSteps(), normalizedGraph);
        }

        if (steps is not { Count: >= 1 })
            throw new InvalidOperationException("At least one step is required.");

        return (OrchestrationGraphRules.NormalizeSteps(steps), null);
    }

    private static IReadOnlyList<int> GetReferencedAgentIds(
        IReadOnlyList<OrchestrationStep> steps,
        OrchestrationGraph? graph)
    {
        return graph is not null
            ? graph.Nodes.Select(node => node.AgentId).Distinct().ToList()
            : steps.Select(step => step.AgentId).Distinct().ToList();
    }

    private static async Task<Dictionary<int, string>> GetAccessibleAgentNamesAsync(
        DataNexusDbContext db,
        string userId,
        IReadOnlyList<int> agentIds,
        CancellationToken ct)
    {
        var agents = await db.Agents
            .Where(agent => agentIds.Contains(agent.Id) &&
                (agent.Scope == SkillScope.Public || agent.OwnerId == userId))
            .Select(agent => new { agent.Id, agent.Name })
            .ToListAsync(ct);

        var nameLookup = agents.ToDictionary(agent => agent.Id, agent => agent.Name);
        var missingIds = agentIds.Where(agentId => !nameLookup.ContainsKey(agentId)).ToList();

        if (missingIds.Count > 0)
            throw new InvalidOperationException(
                $"Orchestration references unknown or inaccessible agents: {string.Join(", ", missingIds)}.");

        return nameLookup;
    }

    private static IReadOnlyList<OrchestrationStep> ApplyAgentNames(
        IReadOnlyList<OrchestrationStep> steps,
        IReadOnlyDictionary<int, string> agentNames)
    {
        return steps
            .Select(step => step with { AgentName = agentNames[step.AgentId] })
            .ToList();
    }

    private static OrchestrationGraph ApplyAgentNames(
        OrchestrationGraph graph,
        IReadOnlyDictionary<int, string> agentNames)
    {
        return graph with
        {
            Nodes = graph.Nodes
                .Select(node => node with { AgentName = agentNames[node.AgentId] })
                .ToList(),
        };
    }
}
