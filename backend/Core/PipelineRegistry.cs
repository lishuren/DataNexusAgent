using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace DataNexus.Core;

public sealed class PipelineRegistry(
    IServiceScopeFactory scopeFactory,
    ILogger<PipelineRegistry> logger)
{
    public async Task<IReadOnlyList<PipelineDefinition>> GetPipelinesForUserAsync(
        string userId, CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DataNexusDbContext>();

        var entities = await db.Pipelines
            .Where(p => p.Scope == SkillScope.Public || p.OwnerId == userId)
            .AsNoTracking()
            .ToListAsync(ct);

        return [.. entities.Select(e => e.ToDefinition())];
    }

    public async Task<IReadOnlyList<PipelineDefinition>> GetPublicPipelinesAsync(
        CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DataNexusDbContext>();

        var entities = await db.Pipelines
            .Where(p => p.Scope == SkillScope.Public)
            .AsNoTracking()
            .ToListAsync(ct);

        return [.. entities.Select(e => e.ToDefinition())];
    }

    public async Task<PipelineDefinition?> GetByIdAsync(int id, CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DataNexusDbContext>();

        var entity = await db.Pipelines.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);
        return entity?.ToDefinition();
    }

    public async Task<PipelineDefinition?> GetByIdForUserAsync(
        string userId, int id, CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DataNexusDbContext>();

        var entity = await db.Pipelines.AsNoTracking().FirstOrDefaultAsync(
            p => p.Id == id && (p.Scope == SkillScope.Public || p.OwnerId == userId), ct);
        return entity?.ToDefinition();
    }

    public async Task<PipelineDefinition> CreateAsync(
        string userId, string name, IReadOnlyList<int> agentIds,
        bool enableSelfCorrection = true, int maxCorrectionAttempts = 3,
        ExecutionMode executionMode = ExecutionMode.Sequential,
        ConcurrentAggregatorMode concurrentAggregatorMode = ConcurrentAggregatorMode.Concatenate,
        CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DataNexusDbContext>();

        await EnsurePipelineNameAvailableAsync(db, userId, name, null, ct);

        var entity = new PipelineEntity
        {
            Name = name,
            AgentIdsJson = JsonSerializer.Serialize(agentIds),
            EnableSelfCorrection = enableSelfCorrection,
            MaxCorrectionAttempts = Math.Clamp(maxCorrectionAttempts, 1, 10),
            ExecutionMode = executionMode,
            ConcurrentAggregatorMode = concurrentAggregatorMode,
            Scope = SkillScope.Private,
            OwnerId = userId,
        };

        db.Pipelines.Add(entity);
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "[User: {UserId}] Created pipeline '{Name}' ({Mode}) with {Count} steps",
            userId, name, executionMode, agentIds.Count);

        return entity.ToDefinition();
    }

    public async Task<PipelineDefinition> UpdateAsync(
        string userId, int pipelineId, string name, IReadOnlyList<int> agentIds,
        bool enableSelfCorrection, int maxCorrectionAttempts,
        ExecutionMode executionMode = ExecutionMode.Sequential,
        ConcurrentAggregatorMode concurrentAggregatorMode = ConcurrentAggregatorMode.Concatenate,
        CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DataNexusDbContext>();

        var entity = await db.Pipelines.FirstOrDefaultAsync(
            p => p.Id == pipelineId && p.OwnerId == userId, ct)
            ?? throw new ResourceNotFoundException($"Pipeline {pipelineId} not found.");

        await EnsurePipelineNameAvailableAsync(db, userId, name, pipelineId, ct);

        entity.Name = name;
        entity.AgentIdsJson = JsonSerializer.Serialize(agentIds);
        entity.EnableSelfCorrection = enableSelfCorrection;
        entity.MaxCorrectionAttempts = Math.Clamp(maxCorrectionAttempts, 1, 10);
        entity.ExecutionMode = executionMode;
        entity.ConcurrentAggregatorMode = concurrentAggregatorMode;
        entity.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "[User: {UserId}] Updated pipeline '{Name}'", userId, name);

        return entity.ToDefinition();
    }

    public async Task DeleteAsync(string userId, int pipelineId, CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DataNexusDbContext>();

        var entity = await db.Pipelines.FirstOrDefaultAsync(
            p => p.Id == pipelineId && p.OwnerId == userId, ct)
            ?? throw new ResourceNotFoundException($"Pipeline {pipelineId} not found.");

        db.Pipelines.Remove(entity);
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "[User: {UserId}] Deleted pipeline '{Name}'", userId, entity.Name);
    }

    public async Task<PipelineDefinition> PublishAsync(
        string userId, int pipelineId, CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DataNexusDbContext>();

        var entity = await db.Pipelines.FirstOrDefaultAsync(
            p => p.Id == pipelineId && p.OwnerId == userId && p.Scope == SkillScope.Private, ct)
            ?? throw new ResourceNotFoundException($"Pipeline {pipelineId} not found.");

        entity.Scope = SkillScope.Public;
        entity.PublishedByUserId = userId;
        entity.OwnerId = null;
        entity.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "[User: {UserId}] Published pipeline '{Name}' to public", userId, entity.Name);

        return entity.ToDefinition();
    }

    public async Task<PipelineDefinition> UnpublishAsync(
        string userId, int pipelineId, CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DataNexusDbContext>();

        var entity = await db.Pipelines.FirstOrDefaultAsync(
            p => p.Id == pipelineId && p.Scope == SkillScope.Public, ct)
            ?? throw new ResourceNotFoundException($"Pipeline {pipelineId} not found.");

        if (!string.Equals(entity.PublishedByUserId, userId, StringComparison.Ordinal))
            throw new ResourceAccessDeniedException($"You cannot unpublish this pipeline.");

        await EnsurePipelineNameAvailableAsync(db, userId, entity.Name, entity.Id, ct);

        entity.Scope = SkillScope.Private;
        entity.OwnerId = userId;
        entity.PublishedByUserId = null;
        entity.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "[User: {UserId}] Unpublished pipeline '{Name}'", userId, entity.Name);

        return entity.ToDefinition();
    }

    public async Task<PipelineDefinition> CloneAsync(
        string userId, int pipelineId, string newName, CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DataNexusDbContext>();

        var source = await db.Pipelines.AsNoTracking().FirstOrDefaultAsync(p => p.Id == pipelineId, ct)
            ?? throw new ResourceNotFoundException($"Pipeline {pipelineId} not found.");

        if (source.Scope != SkillScope.Public && !string.Equals(source.OwnerId, userId, StringComparison.Ordinal))
            throw new ResourceNotFoundException($"Pipeline {pipelineId} not found.");

        await EnsurePipelineNameAvailableAsync(db, userId, newName, null, ct);

        var entity = new PipelineEntity
        {
            Name = newName,
            AgentIdsJson = source.AgentIdsJson,
            EnableSelfCorrection = source.EnableSelfCorrection,
            MaxCorrectionAttempts = source.MaxCorrectionAttempts,
            Scope = SkillScope.Private,
            OwnerId = userId
        };

        db.Pipelines.Add(entity);
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "[User: {UserId}] Cloned pipeline '{Name}' as '{CloneName}'",
            userId, source.Name, newName);

        return entity.ToDefinition();
    }

    private static async Task EnsurePipelineNameAvailableAsync(
        DataNexusDbContext db,
        string userId,
        string name,
        int? excludeId,
        CancellationToken ct)
    {
        var exists = await db.Pipelines.AnyAsync(
            p => p.OwnerId == userId && p.Name == name && (!excludeId.HasValue || p.Id != excludeId.Value),
            ct);

        if (exists)
            throw new ResourceConflictException($"Pipeline name '{name}' already exists.");
    }
}
