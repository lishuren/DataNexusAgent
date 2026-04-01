using System.Runtime.CompilerServices;
using System.Text;
using DataNexus.Core;
using DataNexus.Identity;
using DataNexus.Models;
using DataNexus.Plugins;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;

namespace DataNexus.Agents;

public sealed class DataNexusEngine(
    AgentFactory agentFactory,
    AgentRegistry agentRegistry,
    InputProcessorPlugin inputPlugin,
    OutputIntegratorPlugin outputPlugin,
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

        // Default: two-agent AF workflow (Analyst → Executor)
        return await RunDefaultPipelineAsync(request, user, ct);
    }

    public async IAsyncEnumerable<ProcessingStreamEvent> StreamProcessAsync(
        ProcessingRequest request,
        UserContext user,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        logger.LogInformation(
            "[User: {UserId}] Streaming engine started: agent={AgentId} {Source} → {Destination}",
            user.UserId, request.AgentId, request.InputSource, request.OutputDestination);

        if (request.AgentId is { } agentId)
        {
            ProcessingResult? resolutionFailure = null;
            AgentDefinition? agent;
            try
            {
                agent = await agentRegistry.GetAgentByIdAsync(agentId, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "[User: {UserId}] Failed to resolve agent {AgentId}", user.UserId, agentId);
                agent = null;
                resolutionFailure = ProcessingResult.Fail($"Failed to resolve agent {agentId}: {ex.Message}");
            }

            if (resolutionFailure is not null)
            {
                yield return ProcessingStreamEvent.ResultEvent(resolutionFailure);
                yield break;
            }

            if (agent is null)
            {
                yield return ProcessingStreamEvent.ResultEvent(
                    ProcessingResult.Fail($"Agent {agentId} not found"));
                yield break;
            }

            await foreach (var streamEvent in StreamSingleAgentAsync(agent, request, user, ct))
                yield return streamEvent;

            yield break;
        }

        await foreach (var streamEvent in StreamDefaultPipelineAsync(request, user, ct))
            yield return streamEvent;
    }

    /// <summary>
    /// Runs a multi-agent pipeline. Execution mode is driven by <see cref="PipelineRequest.ExecutionMode"/>:
    /// <list type="bullet">
    ///   <item><see cref="ExecutionMode.Sequential"/> — AF <see cref="AgentWorkflowBuilder.BuildSequential"/>.</item>
    ///   <item><see cref="ExecutionMode.Concurrent"/> — AF <see cref="AgentWorkflowBuilder.BuildConcurrent"/> fan-out/fan-in.</item>
    ///   <item><see cref="ExecutionMode.Handoff"/> — AF <see cref="AgentWorkflowBuilder.CreateHandoffBuilderWith"/> triage routing.</item>
    ///   <item><see cref="ExecutionMode.GroupChat"/> — AF <see cref="AgentWorkflowBuilder.CreateGroupChatBuilderWith"/> round-robin chat.</item>
    /// </list>
    /// </summary>
    public async Task<ProcessingResult> RunPipelineAsync(
        PipelineRequest pipeline,
        UserContext user,
        CancellationToken ct = default)
    {
        logger.LogInformation(
            "[User: {UserId}] Pipeline '{Name}' started ({Mode}) with {Count} agents",
            user.UserId, pipeline.Name, pipeline.ExecutionMode, pipeline.AgentIds.Count);

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
                agentDef, user, inputPlugin, outputPlugin, pipeline.OutputDestination, pipeline.Parameters, ct);
            agents.Add(agent);
        }

        // Build the workflow based on execution mode
        var workflow = pipeline.ExecutionMode switch
        {
            ExecutionMode.Concurrent =>
                AgentWorkflowBuilder.BuildConcurrent(pipeline.Name, agents, BuildConcurrentAggregator(pipeline.ConcurrentAggregatorMode)),

            ExecutionMode.Handoff =>
                BuildHandoffWorkflow(pipeline.Name, agents),

            ExecutionMode.GroupChat =>
                BuildGroupChatWorkflow(pipeline.Name, agents, pipeline.GroupChatMaxIterations),

            _ => // Sequential (default)
                AgentWorkflowBuilder.BuildSequential(pipeline.Name, agents),
        };

        // Execute workflow with retry for self-correction (sequential/concurrent only)
        var maxAttempts = pipeline.EnableSelfCorrection
            && pipeline.ExecutionMode is ExecutionMode.Sequential or ExecutionMode.Concurrent
            ? pipeline.MaxCorrectionAttempts : 1;

        Run? run = null;
        string? output = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            if (attempt > 1)
                logger.LogInformation(
                    "[User: {UserId}] Pipeline '{Name}' self-correction attempt {Attempt}",
                    user.UserId, pipeline.Name, attempt);

            run = await InProcessExecution.RunAsync(workflow, pipeline.InputSource, cancellationToken: ct);
            output = ExtractWorkflowOutput(run);

            if (output is not null && !PluginError.IsPluginError(output))
                break;

            if (!pipeline.EnableSelfCorrection)
                break;
        }

        if (output is null)
            return ProcessingResult.Fail($"Pipeline '{pipeline.Name}' produced no output");

        if (PluginError.IsPluginError(output))
            return ProcessingResult.Fail($"Pipeline '{pipeline.Name}' failed: {output}");

        logger.LogInformation("[User: {UserId}] Pipeline '{Name}' completed ({Mode})",
            user.UserId, pipeline.Name, pipeline.ExecutionMode);

        return ProcessingResult.Ok(
            $"Pipeline '{pipeline.Name}' completed ({pipeline.ExecutionMode})",
            output,
            run?.NewEventCount > 0 ? [$"Workflow produced {run.NewEventCount} events"] : null);
    }

    public async IAsyncEnumerable<ProcessingStreamEvent> StreamPipelineAsync(
        PipelineRequest pipeline,
        UserContext user,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        logger.LogInformation(
            "[User: {UserId}] Streaming pipeline '{Name}' started ({Mode}) with {Count} agents",
            user.UserId, pipeline.Name, pipeline.ExecutionMode, pipeline.AgentIds.Count);

        var agentDefs = new List<AgentDefinition>(pipeline.AgentIds.Count);
        foreach (var agentId in pipeline.AgentIds)
        {
            ProcessingResult? resolutionFailure = null;
            AgentDefinition? agentDef;
            try
            {
                agentDef = await agentRegistry.GetAgentByIdAsync(agentId, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "[User: {UserId}] Failed to resolve pipeline agent {AgentId}", user.UserId, agentId);
                agentDef = null;
                resolutionFailure = ProcessingResult.Fail($"Failed to resolve agent {agentId}: {ex.Message}");
            }

            if (resolutionFailure is not null)
            {
                yield return ProcessingStreamEvent.ResultEvent(resolutionFailure);
                yield break;
            }

            if (agentDef is null)
            {
                yield return ProcessingStreamEvent.ResultEvent(
                    ProcessingResult.Fail($"Agent {agentId} not found"));
                yield break;
            }

            agentDefs.Add(agentDef);
        }

        var agents = new List<AIAgent>(agentDefs.Count);
        foreach (var agentDef in agentDefs)
        {
            var agent = await agentFactory.CreatePipelineAgentAsync(
                agentDef, user, inputPlugin, outputPlugin, pipeline.OutputDestination, pipeline.Parameters, ct);
            agents.Add(agent);
        }

        var workflow = pipeline.ExecutionMode switch
        {
            ExecutionMode.Concurrent =>
                AgentWorkflowBuilder.BuildConcurrent(
                    pipeline.Name,
                    agents,
                    BuildConcurrentAggregator(pipeline.ConcurrentAggregatorMode)),

            ExecutionMode.Handoff => BuildHandoffWorkflow(pipeline.Name, agents),

            ExecutionMode.GroupChat =>
                BuildGroupChatWorkflow(pipeline.Name, agents, pipeline.GroupChatMaxIterations),

            _ => AgentWorkflowBuilder.BuildSequential(pipeline.Name, agents),
        };

        var maxAttempts = pipeline.EnableSelfCorrection
            && pipeline.ExecutionMode is ExecutionMode.Sequential or ExecutionMode.Concurrent
            ? pipeline.MaxCorrectionAttempts
            : 1;

        await foreach (var streamEvent in StreamWorkflowExecutionAsync(
            workflow,
            pipeline.InputSource,
            startedMessage: $"Pipeline '{pipeline.Name}' is running ({pipeline.ExecutionMode}).",
            completedMessage: $"Pipeline '{pipeline.Name}' completed ({pipeline.ExecutionMode})",
            noOutputMessage: $"Pipeline '{pipeline.Name}' produced no output",
            failurePrefix: $"Pipeline '{pipeline.Name}' failed",
            enableSelfCorrection: pipeline.EnableSelfCorrection,
            maxAttempts: maxAttempts,
            ct))
        {
            yield return streamEvent;
        }
    }

    /// <summary>
    /// Extracts the last agent response text from a completed workflow run.
    /// </summary>
    private static string? ExtractWorkflowOutput(Run run)
    {
        return ResolveWorkflowOutput(run.NewEvents);
    }

    private async Task<ProcessingResult> RunSingleAgentAsync(
        AgentDefinition agent,
        ProcessingRequest request,
        UserContext user,
        CancellationToken ct)
    {
        var agentResult = await agentFactory.CreateRuntimeAgentAsync(
            agent,
            user,
            inputPlugin,
            outputPlugin,
            request.OutputDestination,
            request.Parameters,
            ct: ct);

        var response = await agentResult.Agent.RunAsync(request.InputSource, cancellationToken: ct);
        var responseText = response.Text ?? string.Empty;

        return agent.ExecutionType == AgentExecutionType.External
            ? BuildExternalAgentResult(agent, agentResult, responseText)
            : BuildLlmAgentResult(agent, request, agentResult, responseText, user);
    }

    private async IAsyncEnumerable<ProcessingStreamEvent> StreamSingleAgentAsync(
        AgentDefinition agent,
        ProcessingRequest request,
        UserContext user,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var streamEvent in StreamRuntimeAgentAsync(agent, request, user, ct))
            yield return streamEvent;
    }

    private async IAsyncEnumerable<ProcessingStreamEvent> StreamRuntimeAgentAsync(
        AgentDefinition agent,
        ProcessingRequest request,
        UserContext user,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ProcessingResult? startFailure = null;
        CreateRuntimeAgentResult agentResult;
        try
        {
            agentResult = await agentFactory.CreateRuntimeAgentAsync(
                agent,
                user,
                inputPlugin,
                outputPlugin,
                request.OutputDestination,
                request.Parameters,
                ct: ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "[User: {UserId}] Failed to create runtime agent '{Agent}'", user.UserId, agent.Name);
            agentResult = default!;
            startFailure = ProcessingResult.Fail($"Failed to start agent '{agent.Name}': {ex.Message}");
        }

        if (startFailure is not null)
        {
            yield return ProcessingStreamEvent.ResultEvent(startFailure);
            yield break;
        }

        yield return ProcessingStreamEvent.Status($"Agent '{agent.Name}' is running.", agent.Name);

        var streamedResponse = new StringBuilder();

        await foreach (var update in agentResult.Agent.RunStreamingAsync(request.InputSource, cancellationToken: ct)
            .WithCancellation(ct))
        {
            var chunk = update.Text;
            if (string.IsNullOrWhiteSpace(chunk))
                continue;

            streamedResponse.Append(chunk);
            yield return ProcessingStreamEvent.Chunk(chunk, update.AgentId ?? agent.Name);
        }

        if (agentResult.Trace.RawLlmResponse is null)
            agentResult.Trace.RawLlmResponse = streamedResponse.ToString();

        yield return ProcessingStreamEvent.ResultEvent(
            agent.ExecutionType == AgentExecutionType.External
                ? BuildExternalAgentResult(agent, agentResult, streamedResponse.ToString())
                : BuildLlmAgentResult(agent, request, agentResult, streamedResponse.ToString(), user));
    }

    private ProcessingResult BuildLlmAgentResult(
        AgentDefinition agent,
        ProcessingRequest request,
        CreateRuntimeAgentResult agentResult,
        string fallbackResponseText,
        UserContext user)
    {
        var trace = agentResult.Trace;
        var pluginFailure = BuildPluginFailureResult(trace);
        if (pluginFailure is not null)
            return pluginFailure;

        var llmResponse = trace.RawLlmResponse ?? fallbackResponseText;

        logger.LogInformation(
            "[User: {UserId}] Agent '{Agent}' completed",
            user.UserId, agent.Name);

        var skillDetails = agentResult.ResolvedSkills
            .Select(s => new DebugStep(
                Step: s.Name,
                Status: $"{s.Scope}",
                Preview: TruncatePreview(s.Instructions),
                Chars: s.Instructions.Length))
            .ToList();

        var parsedInput = trace.ParsedInput ?? request.InputSource;
        var debugInfo = new ProcessingDebugInfo(
            InputPluginRan: trace.InputPluginRan,
            ParsedInputPreview: TruncatePreview(parsedInput),
            ParsedInputLength: parsedInput.Length,
            SkillsUsed: agent.SkillNames,
            SkillDetails: skillDetails,
            SystemPromptPreview: TruncatePreview(agentResult.BuiltInstructions),
            SystemPromptLength: agentResult.BuiltInstructions.Length,
            RawLlmResponse: llmResponse,
            OutputPluginRan: trace.OutputPluginRan,
            OutputPluginResult: trace.OutputPluginResult);

        return ProcessingResult.Ok(
            $"Agent '{agent.Name}' completed successfully",
            llmResponse,
            debug: debugInfo);
    }

    private static ProcessingResult? BuildPluginFailureResult(AgentExecutionTrace trace)
    {
        if (trace.InputPluginErrorMessage is not null)
            return ProcessingResult.Fail($"Input parsing failed: {trace.InputPluginErrorMessage}");

        if (trace.OutputPluginErrorMessage is not null)
        {
            if (string.Equals(trace.OutputPluginErrorCode, "SCHEMA_MISMATCH", StringComparison.Ordinal))
                return ProcessingResult.Fail($"Schema mismatch: {trace.OutputPluginErrorMessage}");

            return ProcessingResult.Fail($"Output failed: {trace.OutputPluginErrorMessage}");
        }

        return null;
    }

    private static ProcessingResult BuildExternalAgentResult(
        AgentDefinition agent,
        CreateRuntimeAgentResult agentResult,
        string fallbackResponseText)
    {
        var pluginFailure = BuildPluginFailureResult(agentResult.Trace);
        if (pluginFailure is not null)
            return pluginFailure;

        var externalResult = agentResult.ExternalTrace?.LastResult;
        if (externalResult is not null)
        {
            if (externalResult.Success && externalResult.Data is null && !string.IsNullOrWhiteSpace(fallbackResponseText))
                return externalResult with { Data = fallbackResponseText };

            return externalResult;
        }

        if (string.IsNullOrWhiteSpace(fallbackResponseText))
            return ProcessingResult.Fail($"External agent '{agent.Name}' completed without a response.");

        return ProcessingResult.Ok($"External agent '{agent.Name}' completed.", fallbackResponseText);
    }

    private static string TruncatePreview(string value, int maxLength = 1200) =>
        value.Length > maxLength ? value[..maxLength] + "\n…(truncated)" : value;

    /// <summary>Default two-agent relay built as an AF workflow when no explicit agent is selected.</summary>
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
            analyst, user, inputPlugin, outputPlugin, request.OutputDestination, request.Parameters, ct);
        var executorAgent = await agentFactory.CreatePipelineAgentAsync(
            executor, user, inputPlugin, outputPlugin, request.OutputDestination, request.Parameters, ct);

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

            if (output is not null && !PluginError.IsPluginError(output))
                break;
        }

        if (output is null)
            return ProcessingResult.Fail("Default pipeline produced no output");

        if (PluginError.IsPluginError(output))
            return ProcessingResult.Fail($"Default pipeline failed: {output}");

        List<string> warnings = [];
        if (attempt > 1) warnings.Add($"Required {attempt} attempts to resolve issues");

        return ProcessingResult.Ok("Default pipeline completed successfully", output,
            warnings.Count > 0 ? warnings : null);
    }

    private async IAsyncEnumerable<ProcessingStreamEvent> StreamDefaultPipelineAsync(
        ProcessingRequest request,
        UserContext user,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var allAgents = await agentRegistry.GetAgentsForUserAsync(user.UserId, ct);
        var analyst = allAgents.FirstOrDefault(a => a.Name == "Data Analyst");
        var executor = allAgents.FirstOrDefault(a => a.Name == "API Integrator");

        if (analyst is null || executor is null)
        {
            yield return ProcessingStreamEvent.ResultEvent(
                ProcessingResult.Fail("Default agents (Data Analyst, API Integrator) not found"));
            yield break;
        }

        var analystAgent = await agentFactory.CreatePipelineAgentAsync(
            analyst, user, inputPlugin, outputPlugin, request.OutputDestination, request.Parameters, ct);
        var executorAgent = await agentFactory.CreatePipelineAgentAsync(
            executor, user, inputPlugin, outputPlugin, request.OutputDestination, request.Parameters, ct);

        var workflow = AgentWorkflowBuilder.BuildSequential(
            "DefaultPipeline",
            new List<AIAgent> { analystAgent, executorAgent });

        await foreach (var streamEvent in StreamWorkflowExecutionAsync(
            workflow,
            request.InputSource,
            startedMessage: "Default pipeline is running.",
            completedMessage: "Default pipeline completed successfully",
            noOutputMessage: "Default pipeline produced no output",
            failurePrefix: "Default pipeline failed",
            enableSelfCorrection: true,
            maxAttempts: MaxCorrectionAttempts,
            ct))
        {
            yield return streamEvent;
        }
    }

    /// <summary>
    /// Executes an approved orchestration. Execution mode (<see cref="OrchestrationDefinition.ExecutionMode"/>)
    /// controls how agents interact:
    /// <list type="bullet">
    ///   <item><see cref="ExecutionMode.Sequential"/> — linear chain (original behaviour).</item>
    ///   <item><see cref="ExecutionMode.Concurrent"/> — fan-out all steps in parallel, aggregate.</item>
    ///   <item><see cref="ExecutionMode.Handoff"/> — triage step routes to specialist steps via tool calls.</item>
    ///   <item><see cref="ExecutionMode.GroupChat"/> — all steps participate in a round-robin chat.</item>
    /// </list>
    /// </summary>
    public async Task<ProcessingResult> RunOrchestrationAsync(
        OrchestrationDefinition orchestration,
        string inputSource,
        UserContext user,
        CancellationToken ct = default)
    {
        if (orchestration.WorkflowKind == OrchestrationWorkflowKind.Graph)
            return await RunGraphOrchestrationAsync(orchestration, inputSource, user, ct);

        logger.LogInformation(
            "[User: {UserId}] Orchestration '{Name}' started ({Mode}) with {Count} steps",
            user.UserId, orchestration.Name, orchestration.ExecutionMode, orchestration.Steps.Count);

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
            var stepOutputDestination = ResolveOutputDestination(stepParams);

            var afAgent = await agentFactory.CreateOrchestrationStepAgentAsync(
                agentDef, user, inputPlugin, outputPlugin,
                stepOutputDestination, step.PromptOverride, stepParams, ct);
            afAgents.Add(afAgent);
        }

        // 3. Build workflow based on execution mode
        var workflow = orchestration.ExecutionMode switch
        {
            ExecutionMode.Concurrent =>
                AgentWorkflowBuilder.BuildConcurrent(
                    orchestration.Name, afAgents,
                    BuildConcurrentAggregator(ConcurrentAggregatorMode.Concatenate)),

            ExecutionMode.Handoff =>
                BuildHandoffWorkflow(orchestration.Name, afAgents, orchestration.TriageStepNumber),

            ExecutionMode.GroupChat =>
                BuildGroupChatWorkflow(orchestration.Name, afAgents, orchestration.GroupChatMaxIterations),

            _ => // Sequential (default)
                AgentWorkflowBuilder.BuildSequential(orchestration.Name, afAgents),
        };

        // 4. Execute with self-correction retry (sequential/concurrent only)
        var maxAttempts = orchestration.EnableSelfCorrection
            && orchestration.ExecutionMode is ExecutionMode.Sequential or ExecutionMode.Concurrent
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
            output = ExtractWorkflowOutput(run);

            if (output is not null && !PluginError.IsPluginError(output))
                break;

            if (!orchestration.EnableSelfCorrection)
                break;
        }

        if (output is null)
            return ProcessingResult.Fail($"Orchestration '{orchestration.Name}' produced no output");

        if (PluginError.IsPluginError(output))
            return ProcessingResult.Fail($"Orchestration '{orchestration.Name}' failed: {output}");

        logger.LogInformation(
            "[User: {UserId}] Orchestration '{Name}' completed ({Mode}, {StepCount} steps)",
            user.UserId, orchestration.Name, orchestration.ExecutionMode, orchestration.Steps.Count);

        return ProcessingResult.Ok(
            $"Orchestration '{orchestration.Name}' completed ({orchestration.ExecutionMode}, {orchestration.Steps.Count} steps)",
            output,
            run?.NewEventCount > 0 ? [$"Workflow produced {run.NewEventCount} events"] : null);
    }

    public async IAsyncEnumerable<ProcessingStreamEvent> StreamOrchestrationAsync(
        OrchestrationDefinition orchestration,
        string inputSource,
        UserContext user,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (orchestration.WorkflowKind == OrchestrationWorkflowKind.Graph)
        {
            await foreach (var streamEvent in StreamGraphOrchestrationAsync(orchestration, inputSource, user, ct))
                yield return streamEvent;

            yield break;
        }

        logger.LogInformation(
            "[User: {UserId}] Streaming orchestration '{Name}' started ({Mode}) with {Count} steps",
            user.UserId, orchestration.Name, orchestration.ExecutionMode, orchestration.Steps.Count);

        var stepAgentDefs = new List<(OrchestrationStep Step, AgentDefinition Def)>(orchestration.Steps.Count);
        foreach (var step in orchestration.Steps)
        {
            var agentDef = await agentRegistry.GetAgentByIdAsync(step.AgentId, ct);
            if (agentDef is null)
            {
                yield return ProcessingStreamEvent.ResultEvent(
                    ProcessingResult.Fail($"Step {step.StepNumber} references unknown agent {step.AgentId}"));
                yield break;
            }

            stepAgentDefs.Add((step, agentDef));
        }

        var afAgents = new List<AIAgent>(orchestration.Steps.Count);
        foreach (var (step, agentDef) in stepAgentDefs)
        {
            var stepParams = step.Parameters as IReadOnlyDictionary<string, string>;
            var stepOutputDestination = ResolveOutputDestination(stepParams);
            var afAgent = await agentFactory.CreateOrchestrationStepAgentAsync(
                agentDef,
                user,
                inputPlugin,
                outputPlugin,
                stepOutputDestination,
                step.PromptOverride,
                stepParams,
                ct);
            afAgents.Add(afAgent);
        }

        var workflow = orchestration.ExecutionMode switch
        {
            ExecutionMode.Concurrent =>
                AgentWorkflowBuilder.BuildConcurrent(
                    orchestration.Name,
                    afAgents,
                    BuildConcurrentAggregator(ConcurrentAggregatorMode.Concatenate)),

            ExecutionMode.Handoff =>
                BuildHandoffWorkflow(orchestration.Name, afAgents, orchestration.TriageStepNumber),

            ExecutionMode.GroupChat =>
                BuildGroupChatWorkflow(orchestration.Name, afAgents, orchestration.GroupChatMaxIterations),

            _ => AgentWorkflowBuilder.BuildSequential(orchestration.Name, afAgents),
        };

        var maxAttempts = orchestration.EnableSelfCorrection
            && orchestration.ExecutionMode is ExecutionMode.Sequential or ExecutionMode.Concurrent
            ? orchestration.MaxCorrectionAttempts
            : 1;

        await foreach (var streamEvent in StreamWorkflowExecutionAsync(
            workflow,
            inputSource,
            startedMessage: $"Orchestration '{orchestration.Name}' is running ({orchestration.ExecutionMode}).",
            completedMessage: $"Orchestration '{orchestration.Name}' completed ({orchestration.ExecutionMode}, {orchestration.Steps.Count} steps)",
            noOutputMessage: $"Orchestration '{orchestration.Name}' produced no output",
            failurePrefix: $"Orchestration '{orchestration.Name}' failed",
            enableSelfCorrection: orchestration.EnableSelfCorrection,
            maxAttempts: maxAttempts,
            ct))
        {
            yield return streamEvent;
        }
    }

    private async Task<ProcessingResult> RunGraphOrchestrationAsync(
        OrchestrationDefinition orchestration,
        string inputSource,
        UserContext user,
        CancellationToken ct)
    {
        var graph = orchestration.Graph
            ?? throw new InvalidOperationException(
                $"Graph orchestration '{orchestration.Name}' is missing graph data.");

        logger.LogInformation(
            "[User: {UserId}] Graph orchestration '{Name}' started with {Count} nodes",
            user.UserId, orchestration.Name, graph.Nodes.Count);

        var graphNodes = graph.Nodes.OrderBy(node => node.DisplayOrder).ToList();
        var bindings = new Dictionary<string, ExecutorBinding>(StringComparer.Ordinal);

        foreach (var node in graphNodes)
        {
            var agentDef = await agentRegistry.GetAgentByIdAsync(node.AgentId, ct)
                ?? throw new InvalidOperationException(
                    $"Graph node '{node.Id}' references unknown agent {node.AgentId}");

            logger.LogInformation(
                "[User: {UserId}] Graph orchestration '{Name}' building node {NodeId}: {Agent}",
                user.UserId, orchestration.Name, node.Id, agentDef.Name);

            var nodeParams = node.Parameters as IReadOnlyDictionary<string, string>;
            var nodeOutputDestination = ResolveOutputDestination(nodeParams);
            var afAgent = await agentFactory.CreateOrchestrationStepAgentAsync(
                agentDef,
                user,
                inputPlugin,
                outputPlugin,
                nodeOutputDestination,
                node.PromptOverride,
                nodeParams,
                ct);

            ExecutorBinding binding = afAgent;
            bindings[node.Id] = binding;
        }

        var workflow = BuildGraphWorkflow(orchestration.Name, graph, bindings);
        var run = await InProcessExecution.RunAsync(workflow, inputSource, cancellationToken: ct);
        var output = ExtractWorkflowOutput(run);

        if (output is null)
            return ProcessingResult.Fail($"Graph orchestration '{orchestration.Name}' produced no output");

        if (PluginError.IsPluginError(output))
            return ProcessingResult.Fail($"Graph orchestration '{orchestration.Name}' failed: {output}");

        logger.LogInformation(
            "[User: {UserId}] Graph orchestration '{Name}' completed with {Count} nodes",
            user.UserId, orchestration.Name, graph.Nodes.Count);

        return ProcessingResult.Ok(
            $"Graph orchestration '{orchestration.Name}' completed ({graph.Nodes.Count} nodes)",
            output,
            run.NewEventCount > 0 ? [$"Workflow produced {run.NewEventCount} events"] : null);
    }

    private async IAsyncEnumerable<ProcessingStreamEvent> StreamGraphOrchestrationAsync(
        OrchestrationDefinition orchestration,
        string inputSource,
        UserContext user,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var graph = orchestration.Graph;
        if (graph is null)
        {
            yield return ProcessingStreamEvent.ResultEvent(
                ProcessingResult.Fail($"Graph orchestration '{orchestration.Name}' is missing graph data."));
            yield break;
        }

        logger.LogInformation(
            "[User: {UserId}] Streaming graph orchestration '{Name}' started with {Count} nodes",
            user.UserId, orchestration.Name, graph.Nodes.Count);

        var graphNodes = graph.Nodes.OrderBy(node => node.DisplayOrder).ToList();
        var bindings = new Dictionary<string, ExecutorBinding>(StringComparer.Ordinal);

        foreach (var node in graphNodes)
        {
            var agentDef = await agentRegistry.GetAgentByIdAsync(node.AgentId, ct);
            if (agentDef is null)
            {
                yield return ProcessingStreamEvent.ResultEvent(
                    ProcessingResult.Fail($"Graph node '{node.Id}' references unknown agent {node.AgentId}"));
                yield break;
            }

            var nodeParams = node.Parameters as IReadOnlyDictionary<string, string>;
            var nodeOutputDestination = ResolveOutputDestination(nodeParams);
            var afAgent = await agentFactory.CreateOrchestrationStepAgentAsync(
                agentDef,
                user,
                inputPlugin,
                outputPlugin,
                nodeOutputDestination,
                node.PromptOverride,
                nodeParams,
                ct);

            bindings[node.Id] = afAgent;
        }

        var workflow = BuildGraphWorkflow(orchestration.Name, graph, bindings);

        await foreach (var streamEvent in StreamWorkflowExecutionAsync(
            workflow,
            inputSource,
            startedMessage: $"Graph orchestration '{orchestration.Name}' is running.",
            completedMessage: $"Graph orchestration '{orchestration.Name}' completed ({graph.Nodes.Count} nodes)",
            noOutputMessage: $"Graph orchestration '{orchestration.Name}' produced no output",
            failurePrefix: $"Graph orchestration '{orchestration.Name}' failed",
            enableSelfCorrection: false,
            maxAttempts: 1,
            ct))
        {
            yield return streamEvent;
        }
    }

    private async IAsyncEnumerable<ProcessingStreamEvent> StreamWorkflowExecutionAsync(
        Workflow workflow,
        string inputSource,
        string startedMessage,
        string completedMessage,
        string noOutputMessage,
        string failurePrefix,
        bool enableSelfCorrection,
        int maxAttempts,
        [EnumeratorCancellation] CancellationToken ct)
    {
        yield return ProcessingStreamEvent.Status(startedMessage);

        string? output = null;
        string? failureMessage = null;
        var totalEventCount = 0;
        var attemptsUsed = 0;

        for (var attempt = 1; attempt <= Math.Max(1, maxAttempts); attempt++)
        {
            attemptsUsed = attempt;

            if (attempt > 1)
                yield return ProcessingStreamEvent.Status($"Self-correction attempt {attempt} is running.");

            await using var streamingRun = await InProcessExecution.RunStreamingAsync(workflow, inputSource, cancellationToken: ct);

            var streamedExecutors = new HashSet<string>(StringComparer.Ordinal);
            var visibleExecutors = new HashSet<string>(StringComparer.Ordinal);
            var announcedExecutors = new HashSet<string>(StringComparer.Ordinal);
            var completedExecutors = new HashSet<string>(StringComparer.Ordinal);
            var executorBuffers = new Dictionary<string, StringBuilder>(StringComparer.Ordinal);
            string? attemptLastText = null;
            string? attemptOutput = null;
            string? attemptFailure = null;

            await foreach (var workflowEvent in streamingRun.WatchStreamAsync(ct).WithCancellation(ct))
            {
                totalEventCount++;

                switch (workflowEvent)
                {
                    case WorkflowStartedEvent:
                        yield return ProcessingStreamEvent.Status("Workflow started.");
                        break;

                    case ExecutorInvokedEvent executorInvokedEvent:
                        if (announcedExecutors.Add(executorInvokedEvent.ExecutorId))
                            yield return ProcessingStreamEvent.Status(
                                $"Executor '{executorInvokedEvent.ExecutorId}' started.",
                                executorInvokedEvent.ExecutorId);
                        break;

                    case AgentResponseUpdateEvent updateEvent:
                    {
                        var chunk = updateEvent.Update.Text;
                        if (string.IsNullOrWhiteSpace(chunk))
                            break;

                        var executorId = updateEvent.ExecutorId;
                        streamedExecutors.Add(executorId);
                        visibleExecutors.Add(executorId);

                        if (announcedExecutors.Add(executorId))
                            yield return ProcessingStreamEvent.Status(
                                $"Executor '{executorId}' is streaming output.",
                                executorId);

                        if (!executorBuffers.TryGetValue(executorId, out var buffer))
                        {
                            buffer = new StringBuilder();
                            executorBuffers[executorId] = buffer;
                        }

                        buffer.Append(chunk);
                        attemptLastText = buffer.ToString();
                        yield return ProcessingStreamEvent.Chunk(chunk, executorId);
                        break;
                    }

                    case AgentResponseEvent responseEvent:
                    {
                        var responseText = responseEvent.Response.Text;
                        if (string.IsNullOrWhiteSpace(responseText))
                            break;

                        attemptLastText = responseText;

                        if (announcedExecutors.Add(responseEvent.ExecutorId))
                            yield return ProcessingStreamEvent.Status(
                                $"Executor '{responseEvent.ExecutorId}' produced a response.",
                                responseEvent.ExecutorId);

                        if (!streamedExecutors.Contains(responseEvent.ExecutorId))
                        {
                            visibleExecutors.Add(responseEvent.ExecutorId);
                            yield return ProcessingStreamEvent.Chunk(responseText, responseEvent.ExecutorId);
                        }

                        break;
                    }

                    case WorkflowOutputEvent workflowOutputEvent:
                        attemptOutput = ExtractWorkflowEventOutput(workflowOutputEvent);
                        break;

                    case ExecutorCompletedEvent executorCompletedEvent:
                    {
                        var completionText = ExtractExecutorResultOutput(executorCompletedEvent.Data);
                        attemptOutput ??= completionText;

                        if (!string.IsNullOrWhiteSpace(completionText) && !visibleExecutors.Contains(executorCompletedEvent.ExecutorId))
                        {
                            if (announcedExecutors.Add(executorCompletedEvent.ExecutorId))
                                yield return ProcessingStreamEvent.Status(
                                    $"Executor '{executorCompletedEvent.ExecutorId}' produced output.",
                                    executorCompletedEvent.ExecutorId);

                            visibleExecutors.Add(executorCompletedEvent.ExecutorId);
                            yield return ProcessingStreamEvent.Chunk(completionText, executorCompletedEvent.ExecutorId);
                        }

                        if (completedExecutors.Add(executorCompletedEvent.ExecutorId))
                            yield return ProcessingStreamEvent.Status(
                                $"Executor '{executorCompletedEvent.ExecutorId}' completed.",
                                executorCompletedEvent.ExecutorId);
                        break;
                    }

                    case ExecutorFailedEvent executorFailedEvent:
                        attemptFailure = (executorFailedEvent.Data as Exception)?.Message
                            ?? executorFailedEvent.Data?.ToString()
                            ?? $"Executor '{executorFailedEvent.ExecutorId}' failed.";
                        yield return ProcessingStreamEvent.Status(attemptFailure, executorFailedEvent.ExecutorId);
                        break;

                    case WorkflowErrorEvent workflowErrorEvent:
                        attemptFailure = workflowErrorEvent.Exception?.Message ?? "Workflow execution failed.";
                        yield return ProcessingStreamEvent.Status(attemptFailure);
                        break;
                }
            }

            output = attemptOutput ?? attemptLastText;
            failureMessage = attemptFailure;

            if (!string.IsNullOrWhiteSpace(output) && !PluginError.IsPluginError(output))
                break;

            if (!enableSelfCorrection || attempt >= Math.Max(1, maxAttempts))
                break;

            yield return ProcessingStreamEvent.Status("Plugin error detected. Retrying the workflow.");
        }

        yield return ProcessingStreamEvent.ResultEvent(
            BuildStreamingRunResult(
                completedMessage,
                noOutputMessage,
                failurePrefix,
                output,
                failureMessage,
                totalEventCount,
                attemptsUsed));
    }

    private static ProcessingResult BuildStreamingRunResult(
        string completedMessage,
        string noOutputMessage,
        string failurePrefix,
        string? output,
        string? failureMessage,
        int eventCount,
        int attemptsUsed)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            var failure = string.IsNullOrWhiteSpace(failureMessage)
                ? noOutputMessage
                : $"{failurePrefix}: {failureMessage}";

            return ProcessingResult.Fail(failure);
        }

        if (PluginError.IsPluginError(output))
            return ProcessingResult.Fail($"{failurePrefix}: {output}");

        List<string> warnings = [];
        if (eventCount > 0)
            warnings.Add($"Workflow produced {eventCount} streamed events");
        if (attemptsUsed > 1)
            warnings.Add($"Required {attemptsUsed} attempts to resolve issues");

        return ProcessingResult.Ok(
            completedMessage,
            output,
            warnings.Count > 0 ? warnings : null);
    }

    private static string? ResolveWorkflowOutput(IEnumerable<WorkflowEvent> events)
    {
        var workflowOutput = events.OfType<WorkflowOutputEvent>().LastOrDefault();
        if (workflowOutput is not null)
        {
            var outputText = ExtractWorkflowEventOutput(workflowOutput);
            if (outputText is not null)
                return outputText;
        }

        var lastResponse = events.OfType<AgentResponseEvent>().LastOrDefault();
        return lastResponse?.Response.Text;
    }

    private static string? ExtractWorkflowEventOutput(WorkflowOutputEvent workflowOutputEvent) =>
        workflowOutputEvent.Data switch
        {
            null => null,
            string text => text,
            AgentResponse response => response.Text,
            ChatMessage message => message.Text,
            IEnumerable<ChatMessage> messages => messages.LastOrDefault(m => m.Role == ChatRole.Assistant)?.Text,
            _ => workflowOutputEvent.Data.ToString(),
        };

    private static string? ExtractExecutorResultOutput(object? result) =>
        result switch
        {
            null => null,
            AgentResponse response => response.Text,
            ChatMessage message => message.Text,
            IEnumerable<ChatMessage> messages => messages.LastOrDefault(m => m.Role == ChatRole.Assistant)?.Text,
            _ => result.ToString(),
        };

    private static string ResolveOutputDestination(IReadOnlyDictionary<string, string>? parameters)
    {
        if (parameters is null)
            return "stdout";

        if (TryGetParameterValue(parameters, "OutputDestination", out var outputDestination))
            return outputDestination;

        if (TryGetParameterValue(parameters, "Destination", out var destination))
            return destination;

        return "stdout";
    }

    private static bool TryGetParameterValue(
        IReadOnlyDictionary<string, string> parameters,
        string key,
        out string value)
    {
        if (parameters.TryGetValue(key, out var directValue) && !string.IsNullOrWhiteSpace(directValue))
        {
            value = directValue;
            return true;
        }

        foreach (var pair in parameters)
        {
            if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(pair.Value))
            {
                value = pair.Value;
                return true;
            }
        }

        value = string.Empty;
        return false;
    }

    // ── MAF workflow factory helpers ─────────────────────────────────────────────

    private static Workflow BuildGraphWorkflow(
        string name,
        OrchestrationGraph graph,
        IReadOnlyDictionary<string, ExecutorBinding> bindings)
    {
        var rootNodeIds = OrchestrationGraphRules.GetRootNodeIds(graph);
        var terminalNodeIds = OrchestrationGraphRules.GetTerminalNodeIds(graph);

        if (rootNodeIds.Count != 1)
            throw new InvalidOperationException("Graph orchestrations require exactly one start node.");

        if (terminalNodeIds.Count != 1)
            throw new InvalidOperationException("Graph orchestrations require exactly one terminal node.");

        var rootNodeId = rootNodeIds[0];
        var builder = new WorkflowBuilder(bindings[rootNodeId])
            .WithName(name)
            .WithOutputFrom(bindings[terminalNodeIds[0]]);

        foreach (var node in graph.Nodes)
        {
            if (!string.Equals(node.Id, rootNodeId, StringComparison.Ordinal))
                builder.BindExecutor(bindings[node.Id]);
        }

        var incomingByTarget = graph.Edges
            .GroupBy(edge => edge.TargetNodeId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);

        foreach (var edge in graph.Edges)
        {
            var incomingEdges = incomingByTarget[edge.TargetNodeId];
            if (incomingEdges.Count == 1)
                builder.AddEdge(bindings[edge.SourceNodeId], bindings[edge.TargetNodeId]);
        }

        foreach (var entry in incomingByTarget.Where(entry => entry.Value.Count > 1))
        {
            builder.AddFanInBarrierEdge(
                entry.Value.Select(edge => bindings[edge.SourceNodeId]),
                bindings[entry.Key],
                $"Join {entry.Key}");
        }

        return builder.Build(true);
    }

    /// <summary>
    /// Builds a <see cref="Func{T, TResult}"/> that merges concurrent agent outputs.
    /// Each item in <paramref name="mode"/> maps to a different merge strategy.
    /// </summary>
    private static Func<IList<List<ChatMessage>>, List<ChatMessage>> BuildConcurrentAggregator(
        ConcurrentAggregatorMode mode) => mode switch
    {
        ConcurrentAggregatorMode.First => allResults =>
        {
            var first = allResults.FirstOrDefault();
            return first is not null ? first : [];
        },
        ConcurrentAggregatorMode.Last => allResults =>
        {
            var last = allResults.LastOrDefault();
            return last is not null ? last : [];
        },
        _ => // Concatenate — join all last messages into one assistant message
            allResults =>
            {
                var parts = allResults
                    .Select(msgs => msgs.LastOrDefault(m => m.Role == ChatRole.Assistant)?.Text ?? string.Empty)
                    .Where(t => !string.IsNullOrWhiteSpace(t));
                var combined = string.Join("\n\n---\n\n", parts);
                return [new ChatMessage(ChatRole.Assistant, combined)];
            },
    };

    /// <summary>
    /// Builds a handoff workflow: the first agent (or the one at <paramref name="triageStepNumber"/>)
    /// acts as a triage that can route to any of the remaining agents via LLM tool calls.
    /// All specialists can also hand back to the triage, enabling feedback loops.
    /// </summary>
    private static Workflow BuildHandoffWorkflow(string name, List<AIAgent> agents, int triageStepNumber = 1)
    {
        if (agents.Count < 2)
            throw new InvalidOperationException("Handoff workflows require at least 2 agents.");

        // Step numbers are 1-based; clamp to valid range
        var triageIndex = Math.Clamp(triageStepNumber - 1, 0, agents.Count - 1);
        var triage = agents[triageIndex];
        var specialists = agents.Where((_, i) => i != triageIndex).ToList();

        var builder = AgentWorkflowBuilder.CreateHandoffBuilderWith(triage);

        // triage → all specialists (triage decides which specialist to call)
        builder.WithHandoffs(triage, specialists);

        // each specialist → triage (feedback loop: specialist can escalate back)
        foreach (var specialist in specialists)
            builder.WithHandoff(specialist, triage, $"Escalate back to {triage.Name}");

        return builder.Build();
    }

    /// <summary>
    /// Builds a group-chat workflow where all agents participate in a round-robin discussion.
    /// Terminates when <paramref name="maxIterations"/> turns have elapsed.
    /// </summary>
    private static Workflow BuildGroupChatWorkflow(string name, List<AIAgent> agents, int maxIterations = 10)
    {
        if (agents.Count < 2)
            throw new InvalidOperationException("GroupChat workflows require at least 2 agents.");

        var cappedIterations = Math.Clamp(maxIterations, 2, 50);

        return AgentWorkflowBuilder
            .CreateGroupChatBuilderWith(participants =>
                new RoundRobinGroupChatManager(participants,
                    (manager, _, _) => ValueTask.FromResult(
                        manager.IterationCount >= manager.MaximumIterationCount))
                {
                    MaximumIterationCount = cappedIterations,
                })
            .AddParticipants(agents)
            .WithName(name)
            .Build();
    }

}
