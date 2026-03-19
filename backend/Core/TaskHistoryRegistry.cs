using Microsoft.EntityFrameworkCore;

namespace DataNexus.Core;

public sealed class TaskHistoryRegistry(IServiceScopeFactory scopeFactory)
{
    public async Task<IReadOnlyList<TaskHistoryEntity>> GetForUserAsync(
        string userId, int limit = 50, CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DataNexusDbContext>();

        return await db.TaskHistory
            .Where(t => t.OwnerId == userId)
            .OrderByDescending(t => t.CreatedAt)
            .Take(limit)
            .ToListAsync(ct);
    }

    public async Task RecordAsync(TaskHistoryEntity entry, CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DataNexusDbContext>();

        db.TaskHistory.Add(entry);
        await db.SaveChangesAsync(ct);
    }
}
