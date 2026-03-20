using Microsoft.EntityFrameworkCore;

namespace DataNexus.Core;

public sealed class AgentRegistry(
    IServiceScopeFactory scopeFactory,
    ILogger<AgentRegistry> logger)
{
    /// <summary>
    /// Seeds built-in agents on first run and ensures schema is up-to-date.
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DataNexusDbContext>();

        var hasBuiltIn = await db.Agents.AnyAsync(a => a.IsBuiltIn, ct);
        if (!hasBuiltIn)
        {
            db.Agents.AddRange(GetBuiltInAgents());
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Seeded built-in agents into database");
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
            ?? throw new InvalidOperationException($"Agent {agentId} not found for user '{userId}'");

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
            ?? throw new InvalidOperationException($"Agent {agentId} not found for user '{userId}'");

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
            ?? throw new InvalidOperationException($"Agent {agentId} not found");

        if (source.Scope != SkillScope.Public && !string.Equals(source.OwnerId, userId, StringComparison.Ordinal))
            throw new InvalidOperationException($"Agent {agentId} not available for user '{userId}'");

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
            ?? throw new InvalidOperationException($"Agent {agentId} not found for user '{userId}'");

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
            ?? throw new InvalidOperationException($"Agent {agentId} not found");

        if (!string.Equals(entity.PublishedByUserId, userId, StringComparison.Ordinal))
            throw new InvalidOperationException($"Agent {agentId} not owned by user '{userId}'");

        entity.Scope = SkillScope.Private;
        entity.OwnerId = userId;
        entity.PublishedByUserId = null;
        entity.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(ct);

        logger.LogInformation("[User: {UserId}] Unpublished agent '{AgentName}'", userId, entity.Name);
        return entity.ToDefinition();
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
                1. Parse and understand incoming data (Excel, JSON, CSV).
                2. Apply transformation rules from loaded Skills.
                3. Clean and normalize data for downstream processing.
                4. Output structured JSON ready for the next agent in the pipeline.

                Always respond with valid JSON. If data cannot be parsed, explain the error in a JSON
                object: { "error": "description" }.
                """,
            UiSchema = """
                [
                  {"type":"file","name":"inputFile","label":"Upload data file","accept":".xlsx,.xls,.csv,.json"},
                  {"type":"select","name":"outputFormat","label":"Output format","options":["JSON","CSV","SQL"]},
                  {"type":"text","name":"skillName","label":"Skill to apply (optional)","placeholder":"e.g. ExcelToSqlMapping"}
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
                  {"type":"text","name":"endpoint","label":"API Endpoint (HTTPS)","placeholder":"https://api.example.com/v2/records"},
                  {"type":"select","name":"method","label":"HTTP Method","options":["POST","PUT","PATCH"]},
                  {"type":"textarea","name":"headers","label":"Custom Headers (JSON)","placeholder":"{\"X-Api-Key\": \"...\"}"},
                  {"type":"textarea","name":"schema","label":"Expected Schema (JSON)","placeholder":"Paste JSON schema for validation"}
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
                  {"type":"textarea","name":"data","label":"Paste data or provide URL","placeholder":"Paste JSON/CSV data or URL"},
                  {"type":"select","name":"reportFormat","label":"Report format","options":["Markdown","HTML","Plain Text"]},
                  {"type":"select","name":"detail","label":"Detail level","options":["Summary","Standard","Detailed"]},
                  {"type":"text","name":"focusArea","label":"Focus area (optional)","placeholder":"e.g. revenue trends, anomalies"}
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
                  {"type":"file","name":"inputFile","label":"Upload data file","accept":".xlsx,.csv,.json"},
                  {"type":"textarea","name":"rules","label":"Validation rules (optional)","placeholder":"e.g. email must be valid, age > 0"},
                  {"type":"checkbox","name":"checkDuplicates","label":"Check for duplicates","default":true}
                ]
                """,
            Plugins = "InputProcessor",
            Scope = SkillScope.Public,
            IsBuiltIn = true
        }
    ];
}
