namespace DataNexus.Core;

public sealed record SkillDefinition(
    int Id,
    string Name,
    string Instructions,
    SkillScope Scope,
    string? OwnerId = null,
    string? PublishedByUserId = null) : ISkill
{
    public string Description => Name;
}
