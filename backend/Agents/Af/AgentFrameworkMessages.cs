namespace DataNexus.Agents.Af;

/// <summary>
/// Carries the initial user request into the AF workflow graph.
/// Handled by the entry executor of any single-agent or pipeline run.
/// </summary>
public sealed record AgentStepInput(
    string Data,
    string OutputDestination,
    IReadOnlyDictionary<string, string>? Parameters = null);

/// <summary>
/// Result produced by every step in the workflow graph.
/// Flows as input to the next executor via a conditional success edge.
/// </summary>
public sealed record AgentStepOutput(
    bool Success,
    string Message,
    string? Data = null,
    bool RequiresCorrection = false,
    string? MismatchDetails = null);
