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

    /// <summary>Whether this orchestration is a structured list or a graph.</summary>
    public OrchestrationWorkflowKind WorkflowKind { get; set; } = OrchestrationWorkflowKind.Structured;

    /// <summary>Optional graph payload for DAG-style orchestrations.</summary>
    public string? GraphJson { get; set; }

    public OrchestrationStatus Status { get; set; } = OrchestrationStatus.Draft;

    /// <summary>The model that generated the plan (e.g. "gpt-4o").</summary>
    [MaxLength(100)]
    public string? PlannerModel { get; set; }

    /// <summary>Optional planner reasoning or confidence notes.</summary>
    public string? PlannerNotes { get; set; }

    public bool EnableSelfCorrection { get; set; } = true;

    public int MaxCorrectionAttempts { get; set; } = 3;

    /// <summary>How agents in this orchestration execute. Default is sequential.</summary>
    public ExecutionMode ExecutionMode { get; set; } = ExecutionMode.Sequential;

    /// <summary>
    /// For <see cref="ExecutionMode.Handoff"/>: the step number of the triage agent that routes
    /// to specialists. Must refer to a valid step. Defaults to step 1.
    /// </summary>
    public int TriageStepNumber { get; set; } = 1;

    /// <summary>
    /// For <see cref="ExecutionMode.GroupChat"/>: maximum number of chat turns before forced stop.
    /// </summary>
    public int GroupChatMaxIterations { get; set; } = 10;

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
        WorkflowKind,
        string.IsNullOrWhiteSpace(GraphJson)
            ? null
            : JsonSerializer.Deserialize<OrchestrationGraph>(GraphJson),
        Status, PlannerModel, PlannerNotes,
        EnableSelfCorrection, MaxCorrectionAttempts,
        ExecutionMode, TriageStepNumber, GroupChatMaxIterations,
        Scope, OwnerId, PublishedByUserId, ApprovedAt,
        CreatedAt, UpdatedAt);
}

/// <summary>Immutable view of a stored orchestration.</summary>
public sealed record OrchestrationDefinition(
    int Id,
    string Name,
    string Goal,
    IReadOnlyList<OrchestrationStep> Steps,
    OrchestrationWorkflowKind WorkflowKind,
    OrchestrationGraph? Graph,
    OrchestrationStatus Status,
    string? PlannerModel,
    string? PlannerNotes,
    bool EnableSelfCorrection,
    int MaxCorrectionAttempts,
    ExecutionMode ExecutionMode,
    int TriageStepNumber,
    int GroupChatMaxIterations,
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
