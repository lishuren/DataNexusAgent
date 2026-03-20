using System.ComponentModel.DataAnnotations;

namespace DataNexus.Core;

public sealed class SkillEntity
{
    [Key]
    public int Id { get; set; }

    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    public string Instructions { get; set; } = string.Empty;

    public SkillScope Scope { get; set; }

    [MaxLength(200)]
    public string? OwnerId { get; set; }

    [MaxLength(200)]
    public string? PublishedByUserId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public SkillDefinition ToDefinition() => new(Id, Name, Instructions, Scope, OwnerId, PublishedByUserId);
}
