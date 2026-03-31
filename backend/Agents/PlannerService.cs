using System.Text.Json;
using DataNexus.Core;
using DataNexus.Identity;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace DataNexus.Agents;

/// <summary>Result of a planner invocation: the generated steps plus model metadata.</summary>
public sealed record PlanResult(
    IReadOnlyList<OrchestrationStep> Steps,
    string Model,
    string? Notes);

internal sealed class PlannerStructuredPlan
{
    public string? Notes { get; init; }
    public List<PlannerStructuredStep> Steps { get; init; } = [];
}

internal sealed class PlannerStructuredStep
{
    public int StepNumber { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public int AgentId { get; init; }
    public string AgentName { get; init; } = string.Empty;
}

/// <summary>
/// Uses a MAF <see cref="ChatClientAgent"/> to decompose a user goal into an ordered list of
/// <see cref="OrchestrationStep"/>s. Each step is assigned to a specific agent
/// based on agent descriptions and capabilities.
///
/// The planner itself is an AF agent with audit-logging middleware, consistent with
/// how all other agents in the system are constructed.
/// </summary>
public sealed class PlannerService(
    IChatClient chatClient,
    AgentRegistry agentRegistry,
    IConfiguration configuration,
    ILoggerFactory loggerFactory,
    ILogger<PlannerService> logger)
{
    private const string PlannerInstructions = """
        You are the DataNexus orchestration planner.
        Build a concise workflow that uses only the supplied agent catalog and return the result using the required structured output schema.
        Keep titles short, descriptions actionable, and avoid redundant steps.
        """;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    /// <summary>
    /// Ask the LLM to decompose a user goal into agent steps.
    /// </summary>
    /// <param name="goal">Free-text user goal.</param>
    /// <param name="constraints">Optional constraints or preferences.</param>
    /// <param name="limitToAgentIds">Optional: restrict planner to these agents.</param>
    /// <param name="user">Authenticated user context.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<PlanResult> GeneratePlanAsync(
        string goal,
        string? constraints,
        IReadOnlyList<int>? limitToAgentIds,
        ExecutionMode requestedExecutionMode,
        UserContext user,
        CancellationToken ct = default)
    {
        logger.LogInformation(
            "[User: {UserId}] Planner invoked for goal: {Goal} ({Mode})",
            user.UserId, goal.Length > 120 ? goal[..120] + "…" : goal, requestedExecutionMode);

        var allAgents = await agentRegistry.GetAgentsForUserAsync(user.UserId, ct);
        var candidates = limitToAgentIds is { Count: > 0 }
            ? allAgents.Where(a => limitToAgentIds.Contains(a.Id)).ToList()
            : allAgents.ToList();

        if (candidates.Count == 0)
            throw new InvalidOperationException("No agents available for planning.");

        var userMessage = constraints is not null
            ? $"Goal: {goal}\n\nConstraints: {constraints}"
            : $"Goal: {goal}";

        var model = configuration["GitHubModels:Model"] ?? "gpt-4o";
        var plannerLogger = loggerFactory.CreateLogger("Agent.Planner");
        var userId = user.UserId;

        var plannerAgent = new ChatClientAgent(
            chatClient,
            new ChatClientAgentOptions
            {
                Name = "Planner",
                Description = "Plans DataNexus orchestrations using the current agent catalog.",
                ChatOptions = new ChatOptions
                {
                    Instructions = PlannerInstructions,
                },
                AIContextProviders = [new PlannerContextProvider(candidates, requestedExecutionMode)],
            },
            loggerFactory,
            services: null)
            .AsBuilder()
            .Use(
                runFunc: async (messages, session, options, innerAgent, cancellationToken) =>
                {
                    plannerLogger.LogInformation("[User: {UserId}] Planner agent starting", userId);
                    var response = await innerAgent.RunAsync(messages, session, options, cancellationToken);
                    plannerLogger.LogInformation("[User: {UserId}] Planner agent completed", userId);
                    return response;
                },
                runStreamingFunc: (messages, session, options, innerAgent, cancellationToken) =>
                {
                    plannerLogger.LogInformation("[User: {UserId}] Planner agent streaming", userId);
                    return innerAgent.RunStreamingAsync(messages, session, options, cancellationToken);
                })
            .Build();

        PlannerStructuredPlan payload;
        try
        {
            var response = await plannerAgent.RunAsync<PlannerStructuredPlan>(
                userMessage,
                serializerOptions: JsonOpts,
                cancellationToken: ct);
            payload = response.Result;
        }
        catch (Exception ex) when (ex is InvalidOperationException or JsonException)
        {
            logger.LogWarning(ex, "[User: {UserId}] Planner returned invalid structured output", user.UserId);
            throw new InvalidOperationException("Planner returned invalid structured output.", ex);
        }

        var (steps, notes) = NormalizePlanResponse(payload, candidates);

        logger.LogInformation(
            "[User: {UserId}] Planner produced {StepCount} steps ({Mode})",
            user.UserId, steps.Count, requestedExecutionMode);

        return new PlanResult(steps, model, notes);
    }

    /// <summary>
    /// Normalizes the planner's structured output into orchestration steps and validates agent references.
    /// </summary>
    private static (IReadOnlyList<OrchestrationStep> Steps, string? Notes) NormalizePlanResponse(
        PlannerStructuredPlan response,
        IReadOnlyList<AgentDefinition> agents)
    {
        if (response.Steps.Count == 0)
            throw new InvalidOperationException("Planner response contained no steps.");

        var agentLookup = agents.ToDictionary(a => a.Id);
        var steps = new List<OrchestrationStep>(response.Steps.Count);

        foreach (var step in response.Steps)
        {
            var agentId = step.AgentId;

            if (!agentLookup.TryGetValue(agentId, out var agentDef))
                throw new InvalidOperationException(
                    $"Planner referenced unknown agentId={agentId}.");

            steps.Add(new OrchestrationStep
            {
                StepNumber = steps.Count + 1,
                Title = string.IsNullOrWhiteSpace(step.Title)
                    ? $"Step {steps.Count + 1}"
                    : step.Title.Trim(),
                Description = string.IsNullOrWhiteSpace(step.Description)
                    ? $"Use {agentDef.Name} to advance the workflow."
                    : step.Description.Trim(),
                AgentId = agentId,
                AgentName = agentDef.Name,
                IsEdited = false,
                PromptOverride = null,
                Parameters = null,
            });
        }

        return (steps, string.IsNullOrWhiteSpace(response.Notes) ? null : response.Notes.Trim());
    }
}
