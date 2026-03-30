using System.ComponentModel.DataAnnotations;
using System.Text.Json;

namespace DataNexus.Core;

/// <summary>Status lifecycle for an orchestration plan.</summary>
public enum OrchestrationStatus
{
    Draft,
    Approved,
    Rejected,
    Running,
    Completed,
    Failed
}

/// <summary>
/// Represents a planner-generated orchestration stored in the database.
/// An orchestration decomposes a user goal into ordered agent steps that
/// require explicit user approval before execution.
/// </summary>
public sealed class OrchestrationEntity
{
    [Key]
    public int Id { get; set; }

    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    /// <summary>The original user goal that the planner decomposed.</summary>
    [MaxLength(4000)]
    public string Goal { get; set; } = string.Empty;

    /// <summary>JSON array of <see cref="OrchestrationStep"/> objects.</summary>
    public string StepsJson { get; set; } = "[]";

    public OrchestrationStatus Status { get; set; } = OrchestrationStatus.Draft;

    /// <summary>The model that generated the plan (e.g. "gpt-4o").</summary>
    [MaxLength(100)]
    public string? PlannerModel { get; set; }

    /// <summary>Optional planner reasoning or confidence notes.</summary>
    public string? PlannerNotes { get; set; }

    public bool EnableSelfCorrection { get; set; } = true;

    public int MaxCorrectionAttempts { get; set; } = 3;

    public SkillScope Scope { get; set; }

    [MaxLength(200)]
    public string? OwnerId { get; set; }

    [MaxLength(200)]
    public string? PublishedByUserId { get; set; }

    public DateTime? ApprovedAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public OrchestrationDefinition ToDefinition() => new(
        Id, Name, Goal,
        JsonSerializer.Deserialize<List<OrchestrationStep>>(StepsJson) ?? [],
        Status, PlannerModel, PlannerNotes,
        EnableSelfCorrection, MaxCorrectionAttempts,
        Scope, OwnerId, PublishedByUserId, ApprovedAt,
        CreatedAt, UpdatedAt);
}

/// <summary>Immutable view of a stored orchestration.</summary>
public sealed record OrchestrationDefinition(
    int Id,
    string Name,
    string Goal,
    IReadOnlyList<OrchestrationStep> Steps,
    OrchestrationStatus Status,
    string? PlannerModel,
    string? PlannerNotes,
    bool EnableSelfCorrection,
    int MaxCorrectionAttempts,
    SkillScope Scope,
    string? OwnerId,
    string? PublishedByUserId,
    DateTime? ApprovedAt,
    DateTime CreatedAt,
    DateTime UpdatedAt);

/// <summary>A single step within an orchestration plan.</summary>
public sealed record OrchestrationStep
{
    public int StepNumber { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public int AgentId { get; init; }
    public string AgentName { get; init; } = string.Empty;
    public bool IsEdited { get; init; }

    /// <summary>
    /// If the user customized the agent's prompt for this step,
    /// this overrides the agent's stored system prompt at execution time.
    /// </summary>
    public string? PromptOverride { get; init; }

    /// <summary>Extra parameters passed to the agent at execution time.</summary>
    public Dictionary<string, string>? Parameters { get; init; }
}
