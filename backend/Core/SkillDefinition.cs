namespace DataNexus.Core;

public sealed record SkillDefinition(
    int Id,
    string Name,
    string Description,
    string Instructions,
    SkillScope Scope,
    string? OwnerId = null,
    string? PublishedByUserId = null,
    string? PackageDirectory = null) : ISkill;
