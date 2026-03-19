using Microsoft.EntityFrameworkCore;

namespace DataNexus.Core;

public sealed class SkillRegistry(
    IServiceScopeFactory scopeFactory,
    ILogger<SkillRegistry> logger)
{
    /// <summary>
    /// Seeds built-in skills from .github/skills/public/ into the database on first run.
    /// </summary>
    public async Task InitializeAsync(string? seedPath = null, CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DataNexusDbContext>();

        await db.Database.MigrateAsync(ct);

        if (seedPath is not null && Directory.Exists(seedPath))
        {
            foreach (var file in Directory.EnumerateFiles(seedPath, "*.md"))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                var exists = await db.Skills.AnyAsync(
                    s => s.Name == name && s.Scope == SkillScope.Public, ct);

                if (!exists)
                {
                    var content = await File.ReadAllTextAsync(file, ct);
                    db.Skills.Add(new SkillEntity
                    {
                        Name = name,
                        Instructions = content,
                        Scope = SkillScope.Public
                    });
                    logger.LogInformation("Seeded public skill '{SkillName}' from disk", name);
                }
            }

            await db.SaveChangesAsync(ct);
        }

        var publicCount = await db.Skills.CountAsync(s => s.Scope == SkillScope.Public, ct);
        logger.LogInformation("SkillRegistry initialized — {Count} public skills in database", publicCount);
    }

    /// <summary>
    /// Retrieves all skills available to a user (public + private).
    /// Uses C# 13 params collections for optional skill-name filtering.
    /// </summary>
    public async Task<IReadOnlyList<SkillDefinition>> GetSkillsForUserAsync(
        string userId, CancellationToken ct = default, params string[] skillNames)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DataNexusDbContext>();

        var query = db.Skills
            .Where(s => s.Scope == SkillScope.Public || s.OwnerId == userId);

        if (skillNames.Length > 0)
        {
            var names = skillNames.ToArray().ToList();
            query = query.Where(s => names.Contains(s.Name));
        }

        var entities = await query.AsNoTracking().ToListAsync(ct);
        return [.. entities.Select(e => e.ToDefinition())];
    }

    public async Task<IReadOnlyList<SkillDefinition>> GetPublicSkillsAsync(CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DataNexusDbContext>();

        var entities = await db.Skills
            .Where(s => s.Scope == SkillScope.Public)
            .AsNoTracking()
            .ToListAsync(ct);

        return [.. entities.Select(e => e.ToDefinition())];
    }

    public async Task<SkillDefinition> CreateUserSkillAsync(
        string userId, string name, string instructions, CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DataNexusDbContext>();

        var entity = new SkillEntity
        {
            Name = name,
            Instructions = instructions,
            Scope = SkillScope.Private,
            OwnerId = userId
        };

        db.Skills.Add(entity);
        await db.SaveChangesAsync(ct);

        logger.LogInformation("[User: {UserId}] Created skill '{SkillName}'", userId, name);
        return entity.ToDefinition();
    }

    public async Task<SkillDefinition> PublishSkillAsync(
        string userId, string skillName, CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DataNexusDbContext>();

        var entity = await db.Skills.FirstOrDefaultAsync(
            s => s.Name == skillName && s.OwnerId == userId && s.Scope == SkillScope.Private, ct)
            ?? throw new InvalidOperationException($"Skill '{skillName}' not found for user '{userId}'");

        entity.Scope = SkillScope.Public;
        entity.OwnerId = null;
        entity.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(ct);

        logger.LogInformation("[User: {UserId}] Published skill '{SkillName}' to public", userId, skillName);
        return entity.ToDefinition();
    }
}
