using DataNexus.Agents.Af;
using DataNexus.Core;
using DataNexus.Identity;
using DataNexus.Models;
using DataNexus.Plugins;
using Microsoft.Agents.AI.Workflows;

namespace DataNexus.Agents;

/// <summary>
/// IAgentExecutionRuntime implementation using Microsoft Agent Framework.
/// Replaces legacy DataNexusEngine with AgentWorkflowBuilder.BuildSequential-based orchestration.
///
/// Plugin execution is scoped to the runtime boundary, not inside the workflow:
///   • InputProcessorPlugin  — pre-workflow: parses Excel/CSV/JSON before the first agent runs.
///   • OutputIntegratorPlugin — post-workflow: validates schema and routes output after the last agent.
///
/// ProcessAsync: single-agent workflow.
/// RunPipelineAsync: N-agent pipeline with optional self-correction retry loop.
///   AF DAGs forbid back-edges; correction is external — the correction hint is prepended to the
///   input string and the entire workflow re-runs, up to MaxCorrectionAttempts.
/// </summary>
public sealed class AgentFrameworkExecutionRuntime(
    DynamicWorkflowBuilder workflowBuilder,
    AgentRegistry agentRegistry,
    InputProcessorPlugin inputPlugin,
    OutputIntegratorPlugin outputPlugin,
    ILogger<AgentFrameworkExecutionRuntime> logger) : IAgentExecutionRuntime
{
    /// <summary>
    /// Runs a single agent: pre-processes input via <c>InputProcessor</c> (if configured),
    /// executes the agent workflow, then post-processes output via <c>OutputIntegrator</c> (if configured).
    /// </summary>
    public async Task<ProcessingResult> ProcessAsync(
        ProcessingRequest request,
        UserContext user,
        CancellationToken ct = default)
    {
        if (request.AgentId is not { } agentId)
            return ProcessingResult.Fail(
                "No agent selected. Please choose an agent on the Process page before running.");

        logger.LogInformation(
            "[User: {UserId}] AF runtime: process agent={AgentId} {Source} → {Destination}",
            user.UserId, agentId, request.InputSource, request.OutputDestination);

        var agent = await agentRegistry.GetAgentByIdAsync(agentId, ct)
            ?? throw new InvalidOperationException($"Agent {agentId} not found.");

        // Pre-workflow: run InputProcessor if agent configures it.
        var processedInput = request.InputSource;
        if (agent.PluginNames.Contains("InputProcessor"))
        {
            var pluginCtx = new PluginContext(user.UserId, request.InputSource, Metadata: request.Parameters);
            var pluginResult = await inputPlugin.ExecuteAsync(pluginCtx, ct);
            if (!pluginResult.Success)
                return ProcessingResult.Fail($"Input parsing failed: {pluginResult.ErrorMessage}");
            processedInput = pluginResult.Output;
        }

        // Build and run single-agent workflow.
        var workflow = await workflowBuilder.BuildAsync([agent], user, ct);
        await using var run = await InProcessExecution.RunAsync(workflow, processedInput, null, ct);
        var result = WorkflowResultMapper.Map(run, agent.Name);

        if (!result.Success)
            return result;

        // Post-workflow: run OutputIntegrator if agent configures it.
        if (agent.PluginNames.Contains("OutputIntegrator"))
        {
            var outCtx = new PluginContext(
                user.UserId, result.Message,
                request.Parameters?.GetValueOrDefault("Schema"),
                request.Parameters);
            var outResult = await outputPlugin.ExecuteAsync(outCtx, ct);
            if (!outResult.Success)
                return ProcessingResult.Fail($"Output integration failed: {outResult.ErrorMessage}");
        }

        return result;
    }

    /// <summary>
    /// Runs an N-agent pipeline sequentially. Applies <c>InputProcessor</c> to the first agent
    /// and <c>OutputIntegrator</c> to the last agent. When self-correction is enabled and
    /// <c>OutputIntegrator</c> detects a schema mismatch, the entire workflow is retried with
    /// a correction hint prepended to the input, up to <see cref="PipelineRequest.MaxCorrectionAttempts"/>.
    /// </summary>
    public async Task<ProcessingResult> RunPipelineAsync(
        PipelineRequest pipeline,
        UserContext user,
        CancellationToken ct = default)
    {
        logger.LogInformation(
            "[User: {UserId}] AF runtime: pipeline '{Name}' — {Count} agents, self-correction={Enabled}",
            user.UserId, pipeline.Name, pipeline.AgentIds.Count,
            pipeline.EnableSelfCorrection);

        var agents = new List<AgentDefinition>(pipeline.AgentIds.Count);
        foreach (var id in pipeline.AgentIds)
        {
            var agent = await agentRegistry.GetAgentByIdAsync(id, ct)
                ?? throw new InvalidOperationException($"Agent {id} not found in pipeline '{pipeline.Name}'.");
            agents.Add(agent);
        }

        var maxAttempts = pipeline.EnableSelfCorrection
            ? Math.Max(1, pipeline.MaxCorrectionAttempts)
            : 1;

        var firstAgent = agents[0];
        var lastAgent = agents[^1];

        // Pre-workflow: run InputProcessor on the first agent (if it configures it).
        var processedInput = pipeline.InputSource;
        if (firstAgent.PluginNames.Contains("InputProcessor"))
        {
            var pluginCtx = new PluginContext(user.UserId, pipeline.InputSource, Metadata: pipeline.Parameters);
            var pluginResult = await inputPlugin.ExecuteAsync(pluginCtx, ct);
            if (!pluginResult.Success)
                return ProcessingResult.Fail($"Input parsing failed: {pluginResult.ErrorMessage}");
            processedInput = pluginResult.Output;
        }

        ProcessingResult? lastResult = null;

        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            var workflow = await workflowBuilder.BuildAsync(agents, user, ct);
            await using var run = await InProcessExecution.RunAsync(workflow, processedInput, null, ct);
            lastResult = WorkflowResultMapper.Map(run, pipeline.Name);

            if (!lastResult.Success)
                return lastResult;

            // Post-workflow: run OutputIntegrator on the last agent (if it configures it).
            if (lastAgent.PluginNames.Contains("OutputIntegrator"))
            {
                var outCtx = new PluginContext(
                    user.UserId, lastResult.Message,
                    pipeline.Parameters?.GetValueOrDefault("Schema"),
                    pipeline.Parameters);
                var outResult = await outputPlugin.ExecuteAsync(outCtx, ct);

                if (!outResult.Success && outResult.ErrorCode == "SCHEMA_MISMATCH")
                {
                    if (attempt + 1 >= maxAttempts)
                    {
                        logger.LogWarning(
                            "[User: {UserId}] Pipeline '{Name}' exhausted {Max} correction attempt(s)",
                            user.UserId, pipeline.Name, maxAttempts);
                        return ProcessingResult.Fail(
                            $"Schema mismatch after {maxAttempts} attempt(s): {outResult.ErrorMessage}");
                    }

                    logger.LogInformation(
                        "[User: {UserId}] Pipeline '{Name}' requires correction — attempt {Attempt}/{Max}",
                        user.UserId, pipeline.Name, attempt + 1, maxAttempts);

                    // Prepend correction hint to input for the next attempt.
                    processedInput = $"[Correction hint: {outResult.ErrorMessage}]\n\n{processedInput}";
                    continue;
                }

                if (!outResult.Success)
                    return ProcessingResult.Fail($"Output integration failed: {outResult.ErrorMessage}");
            }

            return lastResult;
        }

        return lastResult ?? ProcessingResult.Fail("Pipeline produced no result.");
    }
}