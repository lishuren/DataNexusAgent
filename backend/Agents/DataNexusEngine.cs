using DataNexus.Core;
using DataNexus.Identity;
using DataNexus.Models;
using DataNexus.Plugins;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;

namespace DataNexus.Agents;

public sealed class DataNexusEngine(
    AgentFactory agentFactory,
    AgentRegistry agentRegistry,
    InputProcessorPlugin inputPlugin,
    OutputIntegratorPlugin outputPlugin,
    ExternalProcessRunner externalRunner,
    ILogger<DataNexusEngine> logger) : IAgentExecutionRuntime
{
    private const int MaxCorrectionAttempts = 3;

    /// <summary>
    /// Runs a single agent or the default Analyst→Executor pipeline.
    /// </summary>
    public async Task<ProcessingResult> ProcessAsync(
        ProcessingRequest request,
        UserContext user,
        CancellationToken ct = default)
    {
        logger.LogInformation(
            "[User: {UserId}] Engine started: agent={AgentId} {Source} → {Destination}",
            user.UserId, request.AgentId, request.InputSource, request.OutputDestination);

        // If a specific agent is selected, run it directly
        if (request.AgentId is { } agentId)
        {
            var agent = await agentRegistry.GetAgentByIdAsync(agentId, ct)
                ?? throw new InvalidOperationException($"Agent {agentId} not found");

            return await RunSingleAgentAsync(agent, request, user, ct);
        }

        // Default: classic two-agent relay (Analyst → Executor)
        return await RunDefaultPipelineAsync(request, user, ct);
    }

    /// <summary>
    /// Runs a multi-agent pipeline using AF's <see cref="AgentWorkflowBuilder.BuildSequential"/>.
    /// Each agent is wrapped with its plugins as AF middleware, enabling a pure-AF execution flow.
    /// Self-correction is handled by retrying the workflow when a plugin error is detected.
    /// </summary>
    public async Task<ProcessingResult> RunPipelineAsync(
        PipelineRequest pipeline,
        UserContext user,
        CancellationToken ct = default)
    {
        logger.LogInformation(
            "[User: {UserId}] Pipeline '{Name}' started with {Count} agents",
            user.UserId, pipeline.Name, pipeline.AgentIds.Count);

        // Resolve all agent definitions
        var agentDefs = new List<AgentDefinition>(pipeline.AgentIds.Count);
        foreach (var agentId in pipeline.AgentIds)
        {
            var agentDef = await agentRegistry.GetAgentByIdAsync(agentId, ct)
                ?? throw new InvalidOperationException($"Agent {agentId} not found");
            agentDefs.Add(agentDef);
        }

        // Build AF agents with plugins embedded as middleware
        var agents = new List<AIAgent>(agentDefs.Count);
        foreach (var agentDef in agentDefs)
        {
            var agent = await agentFactory.CreatePipelineAgentAsync(
                agentDef, user, inputPlugin, outputPlugin, pipeline.Parameters, ct);
            agents.Add(agent);
        }

        // Build sequential workflow via AF
        var workflow = AgentWorkflowBuilder.BuildSequential(pipeline.Name, agents);

        // Execute workflow with retry for self-correction
        var maxAttempts = pipeline.EnableSelfCorrection ? pipeline.MaxCorrectionAttempts : 1;
        Run? run = null;
        string? output = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            if (attempt > 1)
                logger.LogInformation(
                    "[User: {UserId}] Pipeline '{Name}' self-correction attempt {Attempt}",
                    user.UserId, pipeline.Name, attempt);

            run = await InProcessExecution.RunAsync(workflow, pipeline.InputSource, cancellationToken: ct);

            // Extract the final output from workflow events
            output = ExtractWorkflowOutput(run);

            if (output is not null && !output.StartsWith("[PLUGIN_ERROR]"))
                break; // Success

            if (!pipeline.EnableSelfCorrection)
                break;
        }

        if (output is null)
            return ProcessingResult.Fail($"Pipeline '{pipeline.Name}' produced no output");

        if (output.StartsWith("[PLUGIN_ERROR]"))
            return ProcessingResult.Fail($"Pipeline '{pipeline.Name}' failed: {output}");

        logger.LogInformation("[User: {UserId}] Pipeline '{Name}' completed", user.UserId, pipeline.Name);

        return ProcessingResult.Ok(
            $"Pipeline '{pipeline.Name}' completed successfully",
            output,
            run?.NewEventCount > 0 ? [$"Workflow produced {run.NewEventCount} events"] : null);
    }

    /// <summary>
    /// Extracts the last agent response text from a completed workflow run.
    /// </summary>
    private static string? ExtractWorkflowOutput(Run run)
    {
        var events = run.NewEvents;
        var lastResponse = events
            .OfType<AgentResponseEvent>()
            .LastOrDefault();

        return lastResponse?.Response.Text;
    }

    private async Task<ProcessingResult> RunSingleAgentAsync(
        AgentDefinition agent,
        ProcessingRequest request,
        UserContext user,
        CancellationToken ct)
    {
        // Route by execution type
        if (agent.ExecutionType == AgentExecutionType.External)
            return await externalRunner.RunAsync(agent, request, user, ct);

        return await RunLlmAgentAsync(agent, request, user, ct);
    }

    private async Task<ProcessingResult> RunLlmAgentAsync(
        AgentDefinition agent,
        ProcessingRequest request,
        UserContext user,
        CancellationToken ct)
    {
        // 1. Run input plugin if agent uses it
        string inputData = request.InputSource;
        if (agent.PluginNames.Contains("InputProcessor"))
        {
            var pluginCtx = new PluginContext(user.UserId, request.InputSource, Metadata: request.Parameters);
            var pluginResult = await inputPlugin.ExecuteAsync(pluginCtx, ct);
            if (!pluginResult.Success)
                return ProcessingResult.Fail($"Input parsing failed: {pluginResult.ErrorMessage}");
            inputData = pluginResult.Output;
        }

        // 2. Create AF agent (resolves skills, builds instructions, applies middleware)
        var aiAgent = await agentFactory.CreateAgentAsync(agent, user, ct);

        // 3. Run the agent via AF
        var response = await aiAgent.RunAsync(inputData, cancellationToken: ct);
        var llmResponse = response.Text ?? string.Empty;

        // 4. Run output plugin if agent uses it
        if (agent.PluginNames.Contains("OutputIntegrator"))
        {
            var outCtx = new PluginContext(
                user.UserId, llmResponse,
                request.Parameters?.GetValueOrDefault("Schema"),
                request.Parameters);

            var outResult = await outputPlugin.ExecuteAsync(outCtx, ct);

            if (!outResult.Success && outResult.ErrorCode == "SCHEMA_MISMATCH")
                return ProcessingResult.Fail($"Schema mismatch: {outResult.ErrorMessage}");

            if (!outResult.Success)
                return ProcessingResult.Fail($"Output failed: {outResult.ErrorMessage}");
        }

        logger.LogInformation(
            "[User: {UserId}] Agent '{Agent}' completed",
            user.UserId, agent.Name);

        return ProcessingResult.Ok(
            $"Agent '{agent.Name}' completed successfully",
            llmResponse);
    }

    /// <summary>The original two-agent relay for backward compatibility, now using AF agents.</summary>
    private async Task<ProcessingResult> RunDefaultPipelineAsync(
        ProcessingRequest request,
        UserContext user,
        CancellationToken ct)
    {
        var allAgents = await agentRegistry.GetAgentsForUserAsync(user.UserId, ct);
        var analyst = allAgents.FirstOrDefault(a => a.Name == "Data Analyst");
        var executor = allAgents.FirstOrDefault(a => a.Name == "API Integrator");

        if (analyst is null || executor is null)
            return ProcessingResult.Fail("Default agents (Data Analyst, API Integrator) not found");

        // Build AF agents with plugins for both steps
        var analystAgent = await agentFactory.CreatePipelineAgentAsync(
            analyst, user, inputPlugin, outputPlugin, request.Parameters, ct);
        var executorAgent = await agentFactory.CreatePipelineAgentAsync(
            executor, user, inputPlugin, outputPlugin, request.Parameters, ct);

        // Build sequential workflow via AF
        var agentList = new List<AIAgent> { analystAgent, executorAgent };
        var workflow = AgentWorkflowBuilder.BuildSequential("DefaultPipeline", agentList);

        // Execute with self-correction retry
        string? output = null;
        var attempt = 0;

        for (attempt = 1; attempt <= MaxCorrectionAttempts; attempt++)
        {
            if (attempt > 1)
                logger.LogInformation(
                    "[User: {UserId}] Self-correction — attempt {Attempt}/{Max}",
                    user.UserId, attempt, MaxCorrectionAttempts);

            var run = await InProcessExecution.RunAsync(workflow, request.InputSource, cancellationToken: ct);
            output = ExtractWorkflowOutput(run);

            if (output is not null && !output.StartsWith("[PLUGIN_ERROR]")
                && !output.Contains("Schema mismatch"))
                break;
        }

        if (output is null)
            return ProcessingResult.Fail("Default pipeline produced no output");

        if (output.StartsWith("[PLUGIN_ERROR]") || output.Contains("Schema mismatch"))
            return ProcessingResult.Fail($"Default pipeline failed: {output}");

        List<string> warnings = [];
        if (attempt > 1) warnings.Add($"Required {attempt} attempts to resolve issues");

        return ProcessingResult.Ok("Default pipeline completed successfully", output,
            warnings.Count > 0 ? warnings : null);
    }
}
