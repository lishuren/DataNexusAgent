using System.Text.Json;
using DataNexus.Core;
using DataNexus.Identity;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace DataNexus.Agents;

/// <summary>Result of a planner invocation: the generated steps plus model metadata.</summary>
public sealed record PlanResult(
    IReadOnlyList<OrchestrationStep> Steps,
    OrchestrationWorkflowKind WorkflowKind,
    OrchestrationGraph? Graph,
    string Model,
    string? Notes);

internal sealed class PlannerStructuredStepsPlan
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

internal sealed class PlannerStructuredGraphPlan
{
    public string? Notes { get; init; }
    public List<PlannerStructuredGraphNode> Nodes { get; init; } = [];
    public List<PlannerStructuredGraphEdge> Edges { get; init; } = [];
}

internal sealed class PlannerStructuredGraphNode
{
    public int StepNumber { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public int AgentId { get; init; }
    public string AgentName { get; init; } = string.Empty;
}

internal sealed class PlannerStructuredGraphEdge
{
    public int SourceStepNumber { get; init; }
    public int TargetStepNumber { get; init; }
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
        OrchestrationWorkflowKind requestedWorkflowKind,
        UserContext user,
        CancellationToken ct = default)
    {
        logger.LogInformation(
            "[User: {UserId}] Planner invoked for goal: {Goal} ({WorkflowKind}, {Mode})",
            user.UserId,
            goal.Length > 120 ? goal[..120] + "…" : goal,
            requestedWorkflowKind,
            requestedExecutionMode);

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
                AIContextProviders = [new PlannerContextProvider(candidates, requestedExecutionMode, requestedWorkflowKind)],
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

        if (requestedWorkflowKind == OrchestrationWorkflowKind.Graph)
        {
            var payload = await RunPlannerAsync<PlannerStructuredGraphPlan>(
                plannerAgent,
                userMessage,
                user.UserId,
                ct);

            var (steps, graph, notes) = NormalizeGraphPlanResponse(payload, candidates);

            logger.LogInformation(
                "[User: {UserId}] Planner produced {StepCount} graph nodes",
                user.UserId,
                steps.Count);

            return new PlanResult(steps, OrchestrationWorkflowKind.Graph, graph, model, notes);
        }

        var structuredPayload = await RunPlannerAsync<PlannerStructuredStepsPlan>(
            plannerAgent,
            userMessage,
            user.UserId,
            ct);

        var (structuredSteps, structuredNotes) = NormalizeStructuredPlanResponse(structuredPayload, candidates);

        logger.LogInformation(
            "[User: {UserId}] Planner produced {StepCount} steps ({Mode})",
            user.UserId,
            structuredSteps.Count,
            requestedExecutionMode);

        return new PlanResult(
            structuredSteps,
            OrchestrationWorkflowKind.Structured,
            null,
            model,
            structuredNotes);
    }

    /// <summary>
    /// Executes the planner against the requested structured schema.
    /// </summary>
    private async Task<TPlan> RunPlannerAsync<TPlan>(
        AIAgent plannerAgent,
        string userMessage,
        string userId,
        CancellationToken ct)
    {
        try
        {
            var response = await plannerAgent.RunAsync<TPlan>(
                userMessage,
                serializerOptions: JsonOpts,
                cancellationToken: ct);
            return response.Result;
        }
        catch (Exception ex) when (ex is InvalidOperationException or JsonException)
        {
            logger.LogWarning(ex, "[User: {UserId}] Planner returned invalid structured output", userId);
            throw new InvalidOperationException("Planner returned invalid structured output.", ex);
        }
    }

    /// <summary>
    /// Normalizes the planner's structured step output into orchestration steps and validates agent references.
    /// </summary>
    private static (IReadOnlyList<OrchestrationStep> Steps, string? Notes) NormalizeStructuredPlanResponse(
        PlannerStructuredStepsPlan response,
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

    /// <summary>
    /// Normalizes the planner's graph output into the persisted DAG representation.
    /// </summary>
    private static (IReadOnlyList<OrchestrationStep> Steps, OrchestrationGraph Graph, string? Notes) NormalizeGraphPlanResponse(
        PlannerStructuredGraphPlan response,
        IReadOnlyList<AgentDefinition> agents)
    {
        if (response.Nodes.Count == 0)
            throw new InvalidOperationException("Planner response contained no graph nodes.");

        var agentLookup = agents.ToDictionary(agent => agent.Id);
        var orderedNodes = response.Nodes
            .OrderBy(node => node.StepNumber)
            .ToList();

        var layout = BuildGraphLayout(orderedNodes, response.Edges);
        var nodeIdByStepNumber = new Dictionary<int, string>();
        var graphNodes = new List<OrchestrationGraphNode>(orderedNodes.Count);

        foreach (var node in orderedNodes)
        {
            if (node.StepNumber <= 0)
                throw new InvalidOperationException("Graph nodes must use positive step numbers.");

            if (!nodeIdByStepNumber.TryAdd(node.StepNumber, $"node-{node.StepNumber}"))
                throw new InvalidOperationException($"Duplicate graph node stepNumber={node.StepNumber}.");

            if (!agentLookup.TryGetValue(node.AgentId, out var agentDef))
                throw new InvalidOperationException(
                    $"Planner referenced unknown agentId={node.AgentId}.");

            var (positionX, positionY) = layout.GetValueOrDefault(
                node.StepNumber,
                (140d + graphNodes.Count * 220d, 120d));

            graphNodes.Add(new OrchestrationGraphNode
            {
                Id = nodeIdByStepNumber[node.StepNumber],
                DisplayOrder = graphNodes.Count + 1,
                Title = string.IsNullOrWhiteSpace(node.Title)
                    ? $"Node {graphNodes.Count + 1}"
                    : node.Title.Trim(),
                Description = string.IsNullOrWhiteSpace(node.Description)
                    ? $"Use {agentDef.Name} to advance the workflow."
                    : node.Description.Trim(),
                AgentId = node.AgentId,
                AgentName = agentDef.Name,
                IsEdited = false,
                PromptOverride = null,
                Parameters = null,
                PositionX = positionX,
                PositionY = positionY,
            });
        }

        var graphEdges = new List<OrchestrationGraphEdge>(response.Edges.Count);
        foreach (var (edge, index) in response.Edges.Select((item, idx) => (item, idx)))
        {
            if (!nodeIdByStepNumber.TryGetValue(edge.SourceStepNumber, out var sourceNodeId))
                throw new InvalidOperationException(
                    $"Graph edge references unknown source step {edge.SourceStepNumber}.");

            if (!nodeIdByStepNumber.TryGetValue(edge.TargetStepNumber, out var targetNodeId))
                throw new InvalidOperationException(
                    $"Graph edge references unknown target step {edge.TargetStepNumber}.");

            graphEdges.Add(new OrchestrationGraphEdge
            {
                Id = $"edge-{index + 1}",
                SourceNodeId = sourceNodeId,
                TargetNodeId = targetNodeId,
            });
        }

        var graph = OrchestrationGraphRules.NormalizeGraph(new OrchestrationGraph(graphNodes, graphEdges));
        return (graph.ToStructuredSteps(), graph, string.IsNullOrWhiteSpace(response.Notes) ? null : response.Notes.Trim());
    }

    private static Dictionary<int, (double X, double Y)> BuildGraphLayout(
        IReadOnlyList<PlannerStructuredGraphNode> nodes,
        IReadOnlyList<PlannerStructuredGraphEdge> edges)
    {
        var stepNumbers = nodes.Select(node => node.StepNumber).ToList();
        var indegree = stepNumbers.ToDictionary(stepNumber => stepNumber, _ => 0);
        var adjacency = stepNumbers.ToDictionary(stepNumber => stepNumber, _ => new List<int>());

        foreach (var edge in edges)
        {
            if (!indegree.ContainsKey(edge.SourceStepNumber) || !indegree.ContainsKey(edge.TargetStepNumber))
                continue;

            adjacency[edge.SourceStepNumber].Add(edge.TargetStepNumber);
            indegree[edge.TargetStepNumber]++;
        }

        var levels = stepNumbers.ToDictionary(stepNumber => stepNumber, _ => 0);
        var queue = new Queue<int>(
            stepNumbers
                .Where(stepNumber => indegree[stepNumber] == 0)
                .OrderBy(stepNumber => stepNumber));

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var target in adjacency[current].OrderBy(stepNumber => stepNumber))
            {
                levels[target] = Math.Max(levels[target], levels[current] + 1);
                indegree[target]--;
                if (indegree[target] == 0)
                    queue.Enqueue(target);
            }
        }

        var rowByLevel = new Dictionary<int, int>();
        var positions = new Dictionary<int, (double X, double Y)>();

        foreach (var node in nodes.OrderBy(node => levels.GetValueOrDefault(node.StepNumber)).ThenBy(node => node.StepNumber))
        {
            var level = levels.GetValueOrDefault(node.StepNumber);
            var row = rowByLevel.GetValueOrDefault(level);
            positions[node.StepNumber] = (140d + level * 280d, 120d + row * 180d);
            rowByLevel[level] = row + 1;
        }

        return positions;
    }
}
