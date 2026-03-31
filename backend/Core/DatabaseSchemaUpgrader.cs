using System.Data.Common;
using Microsoft.EntityFrameworkCore;

namespace DataNexus.Core;

public sealed class DatabaseSchemaUpgrader(ILogger<DatabaseSchemaUpgrader> logger)
{
    public async Task EnsureAsync(DataNexusDbContext db, CancellationToken ct = default)
    {
        var providerName = db.Database.ProviderName ?? string.Empty;

        await db.Database.OpenConnectionAsync(ct);
        try
        {
            if (providerName.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
            {
                await EnsureSqliteAsync(db, ct);
                return;
            }

            if (providerName.Contains("Npgsql", StringComparison.OrdinalIgnoreCase))
            {
                await EnsurePostgresAsync(db, ct);
            }
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    private async Task EnsureSqliteAsync(DataNexusDbContext db, CancellationToken ct)
    {
        var orchestrationColumns = await GetSqliteColumnsAsync(db.Database.GetDbConnection(), "orchestrations", ct);
        var pipelineColumns = await GetSqliteColumnsAsync(db.Database.GetDbConnection(), "pipelines", ct);

        await EnsureColumnAsync(db, orchestrationColumns, "WorkflowKind",
            "ALTER TABLE \"orchestrations\" ADD COLUMN \"WorkflowKind\" TEXT NOT NULL DEFAULT 'Structured';", ct);
        await EnsureColumnAsync(db, orchestrationColumns, "GraphJson",
            "ALTER TABLE \"orchestrations\" ADD COLUMN \"GraphJson\" TEXT NULL;", ct);
        await EnsureColumnAsync(db, orchestrationColumns, "ExecutionMode",
            "ALTER TABLE \"orchestrations\" ADD COLUMN \"ExecutionMode\" TEXT NOT NULL DEFAULT 'Sequential';", ct);
        await EnsureColumnAsync(db, orchestrationColumns, "TriageStepNumber",
            "ALTER TABLE \"orchestrations\" ADD COLUMN \"TriageStepNumber\" INTEGER NOT NULL DEFAULT 1;", ct);
        await EnsureColumnAsync(db, orchestrationColumns, "GroupChatMaxIterations",
            "ALTER TABLE \"orchestrations\" ADD COLUMN \"GroupChatMaxIterations\" INTEGER NOT NULL DEFAULT 10;", ct);

        await EnsureColumnAsync(db, pipelineColumns, "ExecutionMode",
            "ALTER TABLE \"pipelines\" ADD COLUMN \"ExecutionMode\" TEXT NOT NULL DEFAULT 'Sequential';", ct);
        await EnsureColumnAsync(db, pipelineColumns, "ConcurrentAggregatorMode",
            "ALTER TABLE \"pipelines\" ADD COLUMN \"ConcurrentAggregatorMode\" TEXT NOT NULL DEFAULT 'Concatenate';", ct);
    }

    private async Task EnsurePostgresAsync(DataNexusDbContext db, CancellationToken ct)
    {
        var orchestrationColumns = await GetPostgresColumnsAsync(db.Database.GetDbConnection(), "orchestrations", ct);
        var pipelineColumns = await GetPostgresColumnsAsync(db.Database.GetDbConnection(), "pipelines", ct);

        await EnsureColumnAsync(db, orchestrationColumns, "WorkflowKind",
            "ALTER TABLE \"orchestrations\" ADD COLUMN \"WorkflowKind\" text NOT NULL DEFAULT 'Structured';", ct);
        await EnsureColumnAsync(db, orchestrationColumns, "GraphJson",
            "ALTER TABLE \"orchestrations\" ADD COLUMN \"GraphJson\" text NULL;", ct);
        await EnsureColumnAsync(db, orchestrationColumns, "ExecutionMode",
            "ALTER TABLE \"orchestrations\" ADD COLUMN \"ExecutionMode\" text NOT NULL DEFAULT 'Sequential';", ct);
        await EnsureColumnAsync(db, orchestrationColumns, "TriageStepNumber",
            "ALTER TABLE \"orchestrations\" ADD COLUMN \"TriageStepNumber\" integer NOT NULL DEFAULT 1;", ct);
        await EnsureColumnAsync(db, orchestrationColumns, "GroupChatMaxIterations",
            "ALTER TABLE \"orchestrations\" ADD COLUMN \"GroupChatMaxIterations\" integer NOT NULL DEFAULT 10;", ct);

        await EnsureColumnAsync(db, pipelineColumns, "ExecutionMode",
            "ALTER TABLE \"pipelines\" ADD COLUMN \"ExecutionMode\" text NOT NULL DEFAULT 'Sequential';", ct);
        await EnsureColumnAsync(db, pipelineColumns, "ConcurrentAggregatorMode",
            "ALTER TABLE \"pipelines\" ADD COLUMN \"ConcurrentAggregatorMode\" text NOT NULL DEFAULT 'Concatenate';", ct);
    }

    private async Task EnsureColumnAsync(
        DataNexusDbContext db,
        HashSet<string> existingColumns,
        string columnName,
        string sql,
        CancellationToken ct)
    {
        if (existingColumns.Contains(columnName))
            return;

        await db.Database.ExecuteSqlRawAsync(sql, ct);
        existingColumns.Add(columnName);
        logger.LogInformation("Applied schema upgrade: added column {Column}", columnName);
    }

    private static async Task<HashSet<string>> GetSqliteColumnsAsync(
        DbConnection connection,
        string tableName,
        CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info(\"{tableName}\")";

        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            columns.Add(reader.GetString(1));

        return columns;
    }

    private static async Task<HashSet<string>> GetPostgresColumnsAsync(
        DbConnection connection,
        string tableName,
        CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT column_name
            FROM information_schema.columns
            WHERE table_schema = current_schema() AND table_name = @tableName
            """;

        var tableNameParameter = cmd.CreateParameter();
        tableNameParameter.ParameterName = "@tableName";
        tableNameParameter.Value = tableName;
        cmd.Parameters.Add(tableNameParameter);

        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            columns.Add(reader.GetString(0));

        return columns;
    }
}