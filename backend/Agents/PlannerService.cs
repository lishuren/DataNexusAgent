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
        UserContext user,
        CancellationToken ct = default)
    {
        logger.LogInformation(
            "[User: {UserId}] Planner invoked for goal: {Goal}",
            user.UserId, goal.Length > 120 ? goal[..120] + "…" : goal);

        // 1. Gather available agents
        var allAgents = await agentRegistry.GetAgentsForUserAsync(user.UserId, ct);
        var candidates = limitToAgentIds is { Count: > 0 }
            ? allAgents.Where(a => limitToAgentIds.Contains(a.Id)).ToList()
            : allAgents.ToList();

        if (candidates.Count == 0)
            throw new InvalidOperationException("No agents available for planning.");

        // 2. Build agent catalog for the prompt
        var agentCatalog = string.Join("\n", candidates.Select(a =>
            $"- AgentId={a.Id}, Name=\"{a.Name}\", Type={a.ExecutionType}, " +
            $"Description=\"{a.Description}\", Plugins=\"{a.Plugins}\""));

        // 3. Build the planning prompt
        var systemPrompt = $$"""
            You are a task planner for DataNexus, a multi-agent AI system.
            Your job is to decompose the user's goal into a sequence of steps,
            assigning each step to the most suitable agent from the catalog below.

            ## Available Agents
            {{agentCatalog}}

            ## Rules
            1. Each step MUST reference an agentId from the catalog above.
            2. Steps execute sequentially — output of step N becomes input of step N+1.
            3. Use the minimum number of steps needed. Do not over-decompose.
            4. Pick agents whose description and plugins best match the step's purpose.
            5. If a step needs file parsing, prefer agents with the InputProcessor plugin.
            6. If a step needs API/DB output, prefer agents with the OutputIntegrator plugin.
            7. Provide a clear, actionable title and description for each step.

            ## Output Format
            Respond with ONLY valid JSON and nothing else. The format is:
            {
              "notes": "brief reasoning about the plan",
              "steps": [
                {
                  "stepNumber": 1,
                  "title": "short action title",
                  "description": "what this step does and why",
                  "agentId": <id from catalog>,
                  "agentName": "<name from catalog>"
                }
              ]
            }
            """;

        var userMessage = constraints is not null
            ? $"Goal: {goal}\n\nConstraints: {constraints}"
            : $"Goal: {goal}";

        // 4. Build a MAF ChatClientAgent for the planner with audit-logging middleware
        var model = configuration["GitHubModels:Model"] ?? "gpt-4o";
        var plannerLogger = loggerFactory.CreateLogger("Agent.Planner");
        var userId = user.UserId;

        var plannerAgent = new ChatClientAgent(chatClient, name: "Planner", instructions: systemPrompt)
            .AsBuilder()
            .Use(
                runFunc: async (messages, session, options, innerAgent, cancellationToken) =>
                {
                    plannerLogger.LogInformation("[User: {UserId}] Planner agent starting", userId);
                    var response = await innerAgent.RunAsync(messages, session, options, cancellationToken);
                    plannerLogger.LogInformation("[User: {UserId}] Planner agent completed", userId);
                    return response;
                },
                runStreamingFunc: null)
            .Build();

        // 5. Run the planner agent via MAF
        var response = await plannerAgent.RunAsync(userMessage, cancellationToken: ct);
        var responseText = response.Text ?? string.Empty;

        // 6. Parse the response
        var (steps, notes) = ParsePlanResponse(responseText, candidates);

        logger.LogInformation(
            "[User: {UserId}] Planner produced {StepCount} steps",
            user.UserId, steps.Count);

        return new PlanResult(steps, model, notes);
    }

    /// <summary>
    /// Parses the LLM JSON response into OrchestrationSteps, validating agent references.
    /// </summary>
    private static (IReadOnlyList<OrchestrationStep> Steps, string? Notes) ParsePlanResponse(
        string responseText, IReadOnlyList<AgentDefinition> agents)
    {
        // Strip markdown code fences if present
        var json = responseText.Trim();
        if (json.StartsWith("```"))
        {
            var startIdx = json.IndexOf('{');
            var endIdx = json.LastIndexOf('}');
            if (startIdx >= 0 && endIdx > startIdx)
                json = json[startIdx..(endIdx + 1)];
        }

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var notes = root.TryGetProperty("notes", out var notesEl)
            ? notesEl.GetString()
            : null;

        if (!root.TryGetProperty("steps", out var stepsEl))
            throw new InvalidOperationException("Planner response missing 'steps' array.");

        var agentLookup = agents.ToDictionary(a => a.Id);
        var steps = new List<OrchestrationStep>();

        foreach (var stepEl in stepsEl.EnumerateArray())
        {
            var agentId = stepEl.GetProperty("agentId").GetInt32();

            // Validate agent reference
            if (!agentLookup.TryGetValue(agentId, out var agentDef))
                throw new InvalidOperationException(
                    $"Planner referenced unknown agentId={agentId}.");

            steps.Add(new OrchestrationStep
            {
                StepNumber = stepEl.GetProperty("stepNumber").GetInt32(),
                Title = stepEl.GetProperty("title").GetString() ?? string.Empty,
                Description = stepEl.GetProperty("description").GetString() ?? string.Empty,
                AgentId = agentId,
                AgentName = agentDef.Name,
                IsEdited = false,
                PromptOverride = null,
                Parameters = null,
            });
        }

        // Ensure step numbers are sequential
        for (var i = 0; i < steps.Count; i++)
            steps[i] = steps[i] with { StepNumber = i + 1 };

        return (steps, notes);
    }
}
