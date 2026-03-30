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
        bool inputPluginRan = false;
        if (agent.PluginNames.Contains("InputProcessor"))
        {
            var pluginCtx = new PluginContext(user.UserId, request.InputSource, Metadata: request.Parameters);
            var pluginResult = await inputPlugin.ExecuteAsync(pluginCtx, ct);
            if (!pluginResult.Success)
                return ProcessingResult.Fail($"Input parsing failed: {pluginResult.ErrorMessage}");
            inputData = pluginResult.Output;
            inputPluginRan = true;
        }

        // 2. Create AF agent (resolves skills, builds instructions, applies middleware)
        var agentResult = await agentFactory.CreateAgentAsync(agent, user, ct);

        // 3. Run the agent via AF
        var response = await agentResult.Agent.RunAsync(inputData, cancellationToken: ct);
        var llmResponse = response.Text ?? string.Empty;

        // 4. Run output plugin if agent uses it
        bool outputPluginRan = false;
        string? outputPluginResult = null;
        if (agent.PluginNames.Contains("OutputIntegrator"))
        {
            var outCtx = new PluginContext(
                user.UserId, llmResponse,
                request.Parameters?.GetValueOrDefault("Schema"),
                request.Parameters);

            var outResult = await outputPlugin.ExecuteAsync(outCtx, ct);
            outputPluginRan = true;
            outputPluginResult = outResult.Success ? outResult.Output : $"[ERROR: {outResult.ErrorMessage}]";

            if (!outResult.Success && outResult.ErrorCode == "SCHEMA_MISMATCH")
                return ProcessingResult.Fail($"Schema mismatch: {outResult.ErrorMessage}");

            if (!outResult.Success)
                return ProcessingResult.Fail($"Output failed: {outResult.ErrorMessage}");
        }

        logger.LogInformation(
            "[User: {UserId}] Agent '{Agent}' completed",
            user.UserId, agent.Name);

        // Build per-step debug info
        const int previewLen = 1200;
        string Truncate(string s) => s.Length > previewLen ? s[..previewLen] + "\n…(truncated)" : s;

        var skillDetails = agentResult.ResolvedSkills
            .Select(s => new DebugStep(
                Step: s.Name,
                Status: $"{s.Scope}",
                Preview: Truncate(s.Instructions),
                Chars: s.Instructions.Length))
            .ToList();

        var debugInfo = new ProcessingDebugInfo(
            InputPluginRan: inputPluginRan,
            ParsedInputPreview: Truncate(inputData),
            ParsedInputLength: inputData.Length,
            SkillsUsed: agent.SkillNames,
            SkillDetails: skillDetails,
            SystemPromptPreview: Truncate(agentResult.BuiltInstructions),
            SystemPromptLength: agentResult.BuiltInstructions.Length,
            RawLlmResponse: llmResponse,
            OutputPluginRan: outputPluginRan,
            OutputPluginResult: outputPluginResult);

        return ProcessingResult.Ok(
            $"Agent '{agent.Name}' completed successfully",
            llmResponse,
            debug: debugInfo);
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

    /// <summary>
    /// Executes an approved orchestration using MAF's <see cref="AgentWorkflowBuilder.BuildSequential"/>
    /// and <see cref="InProcessExecution.RunAsync"/>. Each step is constructed as an AF
    /// <see cref="AIAgent"/> with plugins embedded as middleware, supporting optional prompt overrides.
    /// Self-correction retries the entire workflow (matching pipeline behavior).
    /// </summary>
    public async Task<ProcessingResult> RunOrchestrationAsync(
        OrchestrationDefinition orchestration,
        string inputSource,
        UserContext user,
        CancellationToken ct = default)
    {
        logger.LogInformation(
            "[User: {UserId}] Orchestration '{Name}' started with {Count} steps",
            user.UserId, orchestration.Name, orchestration.Steps.Count);

        // 1. Resolve agent definitions for every step
        var stepAgentDefs = new List<(OrchestrationStep Step, AgentDefinition Def)>(orchestration.Steps.Count);
        foreach (var step in orchestration.Steps)
        {
            var agentDef = await agentRegistry.GetAgentByIdAsync(step.AgentId, ct)
                ?? throw new InvalidOperationException(
                    $"Step {step.StepNumber} references unknown agent {step.AgentId}");
            stepAgentDefs.Add((step, agentDef));
        }

        // 2. Build AF agents with plugins embedded as middleware (one per step)
        var afAgents = new List<AIAgent>(orchestration.Steps.Count);
        foreach (var (step, agentDef) in stepAgentDefs)
        {
            logger.LogInformation(
                "[User: {UserId}] Orchestration '{Name}' building step {Step}: {Agent}",
                user.UserId, orchestration.Name, step.StepNumber, agentDef.Name);

            var stepParams = step.Parameters as IReadOnlyDictionary<string, string>;

            var afAgent = await agentFactory.CreateOrchestrationStepAgentAsync(
                agentDef, user, inputPlugin, outputPlugin,
                step.PromptOverride, stepParams, ct);
            afAgents.Add(afAgent);
        }

        // 3. Build sequential workflow via MAF
        var workflow = AgentWorkflowBuilder.BuildSequential(orchestration.Name, afAgents);

        // 4. Execute with self-correction retry (same pattern as RunPipelineAsync)
        var maxAttempts = orchestration.EnableSelfCorrection
            ? orchestration.MaxCorrectionAttempts : 1;
        Run? run = null;
        string? output = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            if (attempt > 1)
                logger.LogInformation(
                    "[User: {UserId}] Orchestration '{Name}' self-correction attempt {Attempt}",
                    user.UserId, orchestration.Name, attempt);

            run = await InProcessExecution.RunAsync(workflow, inputSource, cancellationToken: ct);

            // Extract the final output from MAF workflow events
            output = ExtractWorkflowOutput(run);

            if (output is not null && !output.StartsWith("[PLUGIN_ERROR]"))
                break; // Success

            if (!orchestration.EnableSelfCorrection)
                break;
        }

        if (output is null)
            return ProcessingResult.Fail($"Orchestration '{orchestration.Name}' produced no output");

        if (output.StartsWith("[PLUGIN_ERROR]"))
            return ProcessingResult.Fail($"Orchestration '{orchestration.Name}' failed: {output}");

        logger.LogInformation(
            "[User: {UserId}] Orchestration '{Name}' completed ({StepCount} steps)",
            user.UserId, orchestration.Name, orchestration.Steps.Count);

        return ProcessingResult.Ok(
            $"Orchestration '{orchestration.Name}' completed ({orchestration.Steps.Count} steps)",
            output,
            run?.NewEventCount > 0 ? [$"Workflow produced {run.NewEventCount} events"] : null);
    }
}
