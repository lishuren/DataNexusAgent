namespace DataNexus.Agents;

public sealed class AgentRuntimeOptions
{
    public const string SectionName = "AgentRuntime";

    public string Mode { get; set; } = AgentRuntimeMode.Legacy;

    public bool EnableAgentFrameworkPreview { get; set; }
}

public static class AgentRuntimeMode
{
    public const string Legacy = "Legacy";
    public const string AgentFramework = "AgentFramework";
}