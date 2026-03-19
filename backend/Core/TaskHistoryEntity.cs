using System.ComponentModel.DataAnnotations;

namespace DataNexus.Core;

public sealed class TaskHistoryEntity
{
    [Key]
    public int Id { get; set; }

    [MaxLength(200)]
    public string Summary { get; set; } = "";

    public int? AgentId { get; set; }

    [MaxLength(200)]
    public string? AgentName { get; set; }

    public int? PipelineId { get; set; }

    [MaxLength(200)]
    public string? PipelineName { get; set; }

    public bool Success { get; set; }

    [MaxLength(2000)]
    public string Message { get; set; } = "";

    public int? RowCount { get; set; }

    public double DurationMs { get; set; }

    [MaxLength(200)]
    public string? OwnerId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
