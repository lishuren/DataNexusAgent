namespace DataNexus.Core;

public sealed record SkillDefinition(
    string Name,
    string Instructions,
    SkillScope Scope,
    string? OwnerId = null) : ISkill
{
    public string Description => Name;
}
