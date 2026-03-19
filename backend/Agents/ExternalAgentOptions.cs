namespace DataNexus.Agents;

/// <summary>
/// Configuration options for external (CLI/script) agent execution.
/// Bound from the "ExternalAgents" section of appsettings.json.
/// </summary>
public sealed class ExternalAgentOptions
{
    public const string SectionName = "ExternalAgents";

    /// <summary>Master kill-switch for external agent execution.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Allowlist of executable names or absolute paths that may be invoked.
    /// Any command not in this list is rejected before process creation.
    /// </summary>
    public List<string> AllowedCommands { get; set; } = [];

    /// <summary>Hard upper-bound on per-execution timeout (seconds).</summary>
    public int MaxTimeoutSeconds { get; set; } = 120;

    /// <summary>
    /// Directory prefixes where external agents are allowed to set a WorkingDirectory.
    /// Empty means the agent inherits the backend's working directory.
    /// </summary>
    public List<string> AllowedWorkingDirectories { get; set; } = [];
}
