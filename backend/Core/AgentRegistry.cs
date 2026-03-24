using Microsoft.EntityFrameworkCore;

namespace DataNexus.Core;

public sealed class AgentRegistry(
    IServiceScopeFactory scopeFactory,
    ILogger<AgentRegistry> logger)
{
    /// <summary>
    /// Seeds built-in agents on first run and syncs their definitions on subsequent starts
    /// so code changes propagate to existing databases without requiring a reset.
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DataNexusDbContext>();

        var builtIns = GetBuiltInAgents().ToList();
        var hasBuiltIn = await db.Agents.AnyAsync(a => a.IsBuiltIn, ct);

        if (!hasBuiltIn)
        {
            db.Agents.AddRange(builtIns);
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Seeded built-in agents into database");
        }
        else
        {
            // Sync built-in definitions so prompt / UiSchema improvements take effect
            // without requiring a database reset.
            var existing = await db.Agents.Where(a => a.IsBuiltIn).ToListAsync(ct);
            foreach (var entity in existing)
            {
                var seed = builtIns.Find(b => b.Name == entity.Name);
                if (seed is null) continue;
                entity.Icon = seed.Icon;
                entity.Description = seed.Description;
                entity.SystemPrompt = seed.SystemPrompt;
                entity.UiSchema = seed.UiSchema;
                entity.Plugins = seed.Plugins;
                entity.Skills = seed.Skills;
                entity.UpdatedAt = DateTime.UtcNow;
            }
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Synced built-in agent definitions");
        }

        var count = await db.Agents.CountAsync(ct);
        logger.LogInformation("AgentRegistry initialized — {Count} agents in database", count);
    }

    public async Task<IReadOnlyList<AgentDefinition>> GetAgentsForUserAsync(
        string userId, CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DataNexusDbContext>();

        var entities = await db.Agents
            .Where(a => a.Scope == SkillScope.Public || a.OwnerId == userId)
            .AsNoTracking()
            .ToListAsync(ct);

        return [.. entities.Select(e => e.ToDefinition())];
    }

    public async Task<IReadOnlyList<AgentDefinition>> GetPublicAgentsAsync(CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DataNexusDbContext>();

        var entities = await db.Agents
            .Where(a => a.Scope == SkillScope.Public)
            .AsNoTracking()
            .ToListAsync(ct);

        return [.. entities.Select(e => e.ToDefinition())];
    }

    public async Task<AgentDefinition?> GetAgentByIdAsync(int id, CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DataNexusDbContext>();

        var entity = await db.Agents.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, ct);
        return entity?.ToDefinition();
    }

    public async Task<AgentDefinition?> GetAgentByIdForUserAsync(
        string userId, int id, CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DataNexusDbContext>();

        var entity = await db.Agents.AsNoTracking().FirstOrDefaultAsync(
            a => a.Id == id && (a.Scope == SkillScope.Public || a.OwnerId == userId), ct);
        return entity?.ToDefinition();
    }

    public async Task<AgentDefinition> CreateAgentAsync(
        string userId, string name, string icon, string description,
        string systemPrompt, string? uiSchema, string plugins, string skills,
        AgentExecutionType executionType = AgentExecutionType.Llm,
        string? command = null, string? arguments = null,
        string? workingDirectory = null, int timeoutSeconds = 30,
        CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DataNexusDbContext>();

        await EnsureAgentNameAvailableAsync(db, userId, name, null, ct);

        var entity = new AgentEntity
        {
            Name = name,
            Icon = icon,
            Description = description,
            ExecutionType = executionType,
            SystemPrompt = systemPrompt,
            Command = command,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            TimeoutSeconds = timeoutSeconds,
            UiSchema = uiSchema,
            Plugins = plugins,
            Skills = skills,
            Scope = SkillScope.Private,
            OwnerId = userId
        };

        db.Agents.Add(entity);
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "[User: {UserId}] Created {Type} agent '{AgentName}'",
            userId, executionType, name);
        return entity.ToDefinition();
    }

    public async Task<AgentDefinition> UpdateAgentAsync(
        string userId, int agentId, string name, string icon, string description,
        string systemPrompt, string? uiSchema, string plugins, string skills,
        AgentExecutionType executionType = AgentExecutionType.Llm,
        string? command = null, string? arguments = null,
        string? workingDirectory = null, int timeoutSeconds = 30,
        CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DataNexusDbContext>();

        var entity = await db.Agents.FirstOrDefaultAsync(
            a => a.Id == agentId && a.OwnerId == userId && a.Scope == SkillScope.Private && !a.IsBuiltIn, ct)
            ?? throw new ResourceNotFoundException($"Agent {agentId} not found.");

        await EnsureAgentNameAvailableAsync(db, userId, name, agentId, ct);

        entity.Name = name;
        entity.Icon = icon;
        entity.Description = description;
        entity.ExecutionType = executionType;
        entity.SystemPrompt = systemPrompt;
        entity.UiSchema = uiSchema ?? entity.UiSchema;
        entity.Plugins = plugins;
        entity.Skills = skills;
        entity.TimeoutSeconds = timeoutSeconds;
        entity.Command = executionType == AgentExecutionType.External ? command : null;
        entity.Arguments = executionType == AgentExecutionType.External ? arguments : null;
        entity.WorkingDirectory = executionType == AgentExecutionType.External ? workingDirectory : null;
        entity.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "[User: {UserId}] Updated {Type} agent '{AgentName}'",
            userId, executionType, name);
        return entity.ToDefinition();
    }

    public async Task DeleteAgentAsync(string userId, int agentId, CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DataNexusDbContext>();

        var entity = await db.Agents.FirstOrDefaultAsync(
            a => a.Id == agentId && a.OwnerId == userId && a.Scope == SkillScope.Private && !a.IsBuiltIn, ct)
            ?? throw new ResourceNotFoundException($"Agent {agentId} not found.");

        db.Agents.Remove(entity);
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "[User: {UserId}] Deleted agent '{AgentName}'",
            userId, entity.Name);
    }

    public async Task<AgentDefinition> CloneAgentAsync(
        string userId, int agentId, string newName, CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DataNexusDbContext>();

        var source = await db.Agents.AsNoTracking().FirstOrDefaultAsync(a => a.Id == agentId, ct)
            ?? throw new ResourceNotFoundException($"Agent {agentId} not found.");

        if (source.Scope != SkillScope.Public && !string.Equals(source.OwnerId, userId, StringComparison.Ordinal))
            throw new ResourceNotFoundException($"Agent {agentId} not found.");

        await EnsureAgentNameAvailableAsync(db, userId, newName, null, ct);

        var entity = new AgentEntity
        {
            Name = newName,
            Icon = source.Icon,
            Description = source.Description,
            ExecutionType = source.ExecutionType,
            SystemPrompt = source.SystemPrompt,
            Command = source.Command,
            Arguments = source.Arguments,
            WorkingDirectory = source.WorkingDirectory,
            TimeoutSeconds = source.TimeoutSeconds,
            UiSchema = source.UiSchema,
            Plugins = source.Plugins,
            Skills = source.Skills,
            Scope = SkillScope.Private,
            OwnerId = userId,
            IsBuiltIn = false
        };

        db.Agents.Add(entity);
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "[User: {UserId}] Cloned agent '{AgentName}' as '{CloneName}'",
            userId, source.Name, newName);
        return entity.ToDefinition();
    }

    public async Task<AgentDefinition> PublishAgentAsync(
        string userId, int agentId, CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DataNexusDbContext>();

        var entity = await db.Agents.FirstOrDefaultAsync(
            a => a.Id == agentId && a.OwnerId == userId && a.Scope == SkillScope.Private, ct)
            ?? throw new ResourceNotFoundException($"Agent {agentId} not found.");

        entity.Scope = SkillScope.Public;
        entity.PublishedByUserId = userId;
        entity.OwnerId = null;
        entity.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(ct);

        logger.LogInformation("[User: {UserId}] Published agent '{AgentName}' to public", userId, entity.Name);
        return entity.ToDefinition();
    }

    public async Task<AgentDefinition> UnpublishAgentAsync(
        string userId, int agentId, CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DataNexusDbContext>();

        var entity = await db.Agents.FirstOrDefaultAsync(
            a => a.Id == agentId && a.Scope == SkillScope.Public, ct)
            ?? throw new ResourceNotFoundException($"Agent {agentId} not found.");

        if (!string.Equals(entity.PublishedByUserId, userId, StringComparison.Ordinal))
            throw new ResourceAccessDeniedException($"You cannot unpublish this agent.");

        await EnsureAgentNameAvailableAsync(db, userId, entity.Name, entity.Id, ct);

        entity.Scope = SkillScope.Private;
        entity.OwnerId = userId;
        entity.PublishedByUserId = null;
        entity.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(ct);

        logger.LogInformation("[User: {UserId}] Unpublished agent '{AgentName}'", userId, entity.Name);
        return entity.ToDefinition();
    }

    private static async Task EnsureAgentNameAvailableAsync(
        DataNexusDbContext db,
        string userId,
        string name,
        int? excludeId,
        CancellationToken ct)
    {
        var exists = await db.Agents.AnyAsync(
            a => a.OwnerId == userId && a.Name == name && (!excludeId.HasValue || a.Id != excludeId.Value),
            ct);

        if (exists)
            throw new ResourceConflictException($"Agent name '{name}' already exists.");
    }

    private static IEnumerable<AgentEntity> GetBuiltInAgents() =>
    [
        new()
        {
            Name = "Data Analyst",
            Icon = "🧠",
            Description = "Parse, clean, and transform data with skill-based rules",
            SystemPrompt = """
                You are the DataNexus Analyst Agent. Your role is to:
                1. Parse and understand the incoming data (Excel, CSV, JSON).
                2. If the user provided a "task" parameter, follow it precisely as the primary
                   transformation goal — it overrides any default behaviour.
                3. Apply transformation rules from any loaded Skills.
                4. Clean and normalize data: fix types, trim whitespace, deduplicate rows,
                   resolve nulls with sensible defaults.
                5. Produce output in the format requested by the "outputFormat" parameter
                   (JSON, CSV, or SQL INSERT statements). Default to JSON if not specified.

                Output rules:
                - Respond ONLY with the transformed data — no prose, no markdown fences.
                - For JSON: emit a valid JSON array or object.
                - For CSV: emit header row + data rows, comma-separated.
                - For SQL: emit INSERT INTO statements using the column names from the source data.
                - If data cannot be parsed or the task cannot be completed, respond with a JSON
                  error object: { "error": "<concise description>" }.
                """,
            UiSchema = """
                [
                  {"key":"file","type":"file","label":"Data File","accept":".xlsx,.xls,.csv,.json,.txt,.md,.xml,.tsv","required":true},
                  {"key":"task","type":"textarea","label":"Task Description","placeholder":"Describe what you want done, e.g.\n• Rename columns to snake_case\n• Filter rows where status = active\n• Convert dates to ISO 8601"},
                  {"key":"outputFormat","type":"select","label":"Output Format","options":["JSON","CSV","SQL"]},
                  {"key":"skill","type":"text","label":"Skill to apply (optional)","placeholder":"e.g. ExcelToSqlMapping"}
                ]
                """,
            Plugins = "InputProcessor",
            Skills = "ExcelToSqlMapping",
            Scope = SkillScope.Public,
            IsBuiltIn = true
        },
        new()
        {
            Name = "API Integrator",
            Icon = "🚀",
            Description = "Validate schemas and push data to REST APIs or databases",
            SystemPrompt = """
                You are the DataNexus Executor Agent. Your role is to:
                1. Validate processed data against destination schemas.
                2. Ensure data integrity before writing to APIs or databases.
                3. Detect schema mismatches and report them clearly.
                4. Execute output operations (API calls, DB writes).

                If a schema mismatch is detected, respond ONLY with JSON:
                { "requiresCorrection": true, "mismatchDetails": "description of the issue" }
                Otherwise, respond with:
                { "requiresCorrection": false, "summary": "execution summary" }
                """,
            UiSchema = """
                [
                  {"key":"endpoint","type":"url","label":"API Endpoint (HTTPS)","placeholder":"https://api.example.com/v2/records"},
                  {"key":"method","type":"select","label":"HTTP Method","options":["POST","PUT","PATCH"]},
                  {"key":"headers","type":"textarea","label":"Custom Headers (JSON)","placeholder":"{\"X-Api-Key\": \"...\"}"},
                  {"key":"schema","type":"textarea","label":"Expected Schema (JSON)","placeholder":"Paste JSON schema for validation"}
                ]
                """,
            Plugins = "OutputIntegrator",
            Scope = SkillScope.Public,
            IsBuiltIn = true
        },
        new()
        {
            Name = "Report Writer",
            Icon = "📝",
            Description = "Analyze data and generate summaries or reports",
            SystemPrompt = """
                You are the DataNexus Report Writer Agent. Your role is to:
                1. Analyze incoming structured data.
                2. Identify trends, outliers, and key statistics.
                3. Generate a well-formatted markdown report.
                4. Include summary tables and actionable insights.

                Always output valid Markdown. Structure with headings, lists, and tables.
                """,
            UiSchema = """
                [
                  {"key":"data","type":"textarea","label":"Paste data or provide URL","placeholder":"Paste JSON/CSV data or URL"},
                  {"key":"reportFormat","type":"select","label":"Report format","options":["Markdown","HTML","Plain Text"]},
                  {"key":"detail","type":"select","label":"Detail level","options":["Summary","Standard","Detailed"]},
                  {"key":"focusArea","type":"text","label":"Focus area (optional)","placeholder":"e.g. revenue trends, anomalies"}
                ]
                """,
            Plugins = "InputProcessor",
            Scope = SkillScope.Public,
            IsBuiltIn = true
        },
        new()
        {
            Name = "Data Validator",
            Icon = "🔍",
            Description = "Check data quality, find duplicates, flag anomalies",
            SystemPrompt = """
                You are the DataNexus Data Validator Agent. Your role is to:
                1. Inspect incoming data for quality issues.
                2. Identify missing values, type mismatches, and duplicates.
                3. Flag anomalies and outliers.
                4. Output a validation report as JSON with issues and severity levels.

                Respond with: { "valid": true/false, "issues": [...], "stats": {...} }
                """,
            UiSchema = """
                [
                  {"key":"file","type":"file","label":"Upload data file","accept":".xlsx,.csv,.json","required":true},
                  {"key":"rules","type":"textarea","label":"Validation rules (optional)","placeholder":"e.g. email must be valid, age > 0"},
                  {"key":"checkDuplicates","type":"toggle","label":"Check for duplicates","default":"true"}
                ]
                """,
            Plugins = "InputProcessor",
            Scope = SkillScope.Public,
            IsBuiltIn = true
        }
    ];
}
