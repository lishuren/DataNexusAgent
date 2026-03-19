using System.ComponentModel.DataAnnotations;

namespace DataNexus.Core;

public enum AgentExecutionType
{
    /// <summary>LLM-based agent — executed via the AI inference client.</summary>
    Llm = 0,

    /// <summary>External agent — executed as a local CLI / script process.</summary>
    External = 1
}

/// <summary>
/// Represents a user-composable agent stored in the database.
/// Each agent has a system prompt, optional UI schema, and attached plugins/skills.
/// External agents additionally carry a Command, Arguments, and WorkingDirectory.
/// </summary>
public sealed class AgentEntity
{
    [Key]
    public int Id { get; set; }

    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(10)]
    public string Icon { get; set; } = "🤖";

    [MaxLength(500)]
    public string Description { get; set; } = string.Empty;

    /// <summary>Determines whether this agent runs via the LLM or as an external process.</summary>
    public AgentExecutionType ExecutionType { get; set; } = AgentExecutionType.Llm;

    /// <summary>The system prompt that defines this agent's behavior (LLM agents).</summary>
    public string SystemPrompt { get; set; } = string.Empty;

    // ── External-agent fields ────────────────────────────────────────────

    /// <summary>Executable or script path (external agents only, e.g. "python3", "/usr/bin/myagent").</summary>
    [MaxLength(500)]
    public string? Command { get; set; }

    /// <summary>Arguments template, may include {input} placeholder (external agents only).</summary>
    [MaxLength(2000)]
    public string? Arguments { get; set; }

    /// <summary>Optional working directory for the process.</summary>
    [MaxLength(500)]
    public string? WorkingDirectory { get; set; }

    /// <summary>Max seconds the process may run before being killed (default 30).</summary>
    public int TimeoutSeconds { get; set; } = 30;

    // ── Common fields ────────────────────────────────────────────────────

    /// <summary>
    /// JSON array describing custom UI fields for this agent.
    /// Example: [{"type":"file","label":"Upload data","accept":".xlsx,.csv"},
    ///           {"type":"select","label":"Format","options":["JSON","CSV"]}]
    /// </summary>
    public string? UiSchema { get; set; }

    /// <summary>Comma-separated plugin names (e.g. "InputProcessor,OutputIntegrator").</summary>
    [MaxLength(500)]
    public string Plugins { get; set; } = string.Empty;

    /// <summary>Comma-separated skill names to inject into the system prompt.</summary>
    [MaxLength(500)]
    public string Skills { get; set; } = string.Empty;

    public SkillScope Scope { get; set; }

    [MaxLength(200)]
    public string? OwnerId { get; set; }

    public bool IsBuiltIn { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public AgentDefinition ToDefinition() => new(
        Id, Name, Icon, Description, ExecutionType, SystemPrompt,
        Command, Arguments, WorkingDirectory, TimeoutSeconds,
        UiSchema, Plugins, Skills, Scope, OwnerId, IsBuiltIn);
}

public sealed record AgentDefinition(
    int Id,
    string Name,
    string Icon,
    string Description,
    AgentExecutionType ExecutionType,
    string SystemPrompt,
    string? Command,
    string? Arguments,
    string? WorkingDirectory,
    int TimeoutSeconds,
    string? UiSchema,
    string Plugins,
    string Skills,
    SkillScope Scope,
    string? OwnerId,
    bool IsBuiltIn)
{
    public IReadOnlyList<string> PluginNames =>
        string.IsNullOrWhiteSpace(Plugins) ? [] : [.. Plugins.Split(',', StringSplitOptions.TrimEntries)];

    public IReadOnlyList<string> SkillNames =>
        string.IsNullOrWhiteSpace(Skills) ? [] : [.. Skills.Split(',', StringSplitOptions.TrimEntries)];
}
