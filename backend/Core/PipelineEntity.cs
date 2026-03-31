using System.ComponentModel.DataAnnotations;
using System.Text.Json;

namespace DataNexus.Core;

/// <summary>Controls how agents execute within a pipeline or orchestration.</summary>
public enum ExecutionMode
{
    /// <summary>Agents run one after another — output of each feeds the next.</summary>
    Sequential = 0,

    /// <summary>Agents run in parallel and their outputs are aggregated.</summary>
    Concurrent = 1,

    /// <summary>A triage agent routes to specialist agents via LLM-driven handoffs.</summary>
    Handoff = 2,

    /// <summary>All agents participate in a round-robin group chat, managed by an iteration cap.</summary>
    GroupChat = 3,
}

/// <summary>Controls how concurrent agent outputs are merged into a single result.</summary>
public enum ConcurrentAggregatorMode
{
    /// <summary>Concatenate all agents' final responses into one message.</summary>
    Concatenate = 0,

    /// <summary>Use only the first agent's response.</summary>
    First = 1,

    /// <summary>Use only the last agent's response.</summary>
    Last = 2,
}

/// <summary>
/// Represents a saved agent pipeline stored in the database.
/// A pipeline chains multiple agents — execution order depends on <see cref="ExecutionMode"/>.
/// </summary>
public sealed class PipelineEntity
{
    [Key]
    public int Id { get; set; }

    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    /// <summary>JSON array of agent IDs that form the pipeline, e.g. [1,4,2].</summary>
    [MaxLength(2000)]
    public string AgentIdsJson { get; set; } = "[]";

    public bool EnableSelfCorrection { get; set; } = true;

    public int MaxCorrectionAttempts { get; set; } = 3;

    /// <summary>How agents in this pipeline execute. Default is sequential.</summary>
    public ExecutionMode ExecutionMode { get; set; } = ExecutionMode.Sequential;

    /// <summary>Only relevant when <see cref="ExecutionMode"/> is <see cref="ExecutionMode.Concurrent"/>.</summary>
    public ConcurrentAggregatorMode ConcurrentAggregatorMode { get; set; } = ConcurrentAggregatorMode.Concatenate;

    public SkillScope Scope { get; set; }

    [MaxLength(200)]
    public string? OwnerId { get; set; }

    [MaxLength(200)]
    public string? PublishedByUserId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public PipelineDefinition ToDefinition() => new(
        Id, Name,
        JsonSerializer.Deserialize<List<int>>(AgentIdsJson) ?? [],
        EnableSelfCorrection, MaxCorrectionAttempts,
        ExecutionMode, ConcurrentAggregatorMode,
        Scope, OwnerId, PublishedByUserId);
}

public sealed record PipelineDefinition(
    int Id,
    string Name,
    IReadOnlyList<int> AgentIds,
    bool EnableSelfCorrection,
    int MaxCorrectionAttempts,
    ExecutionMode ExecutionMode,
    ConcurrentAggregatorMode ConcurrentAggregatorMode,
    SkillScope Scope,
    string? OwnerId,
    string? PublishedByUserId);
