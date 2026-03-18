namespace DataNexus.Core;

public interface ISkill
{
    string Name { get; }
    string Description { get; }
    SkillScope Scope { get; }
    string Instructions { get; }
    string? OwnerId { get; }
}

public enum SkillScope
{
    Public,
    Private
}
