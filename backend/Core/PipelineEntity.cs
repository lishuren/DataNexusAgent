using System.ComponentModel.DataAnnotations;
using System.Text.Json;

namespace DataNexus.Core;

/// <summary>
/// Represents a saved agent pipeline stored in the database.
/// A pipeline chains multiple agents sequentially — output of each feeds into the next.
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
        Scope, OwnerId, PublishedByUserId);
}

public sealed record PipelineDefinition(
    int Id,
    string Name,
    IReadOnlyList<int> AgentIds,
    bool EnableSelfCorrection,
    int MaxCorrectionAttempts,
    SkillScope Scope,
    string? OwnerId,
    string? PublishedByUserId);
