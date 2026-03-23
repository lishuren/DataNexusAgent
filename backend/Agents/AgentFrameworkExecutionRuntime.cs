using DataNexus.Agents.Af;
using DataNexus.Core;
using DataNexus.Identity;
using DataNexus.Models;
using Microsoft.Agents.AI.Workflows;

namespace DataNexus.Agents;

/// <summary>
/// IAgentExecutionRuntime implementation that uses the Microsoft Agent Framework.
/// Replaces the legacy DataNexusEngine orchestration with typed AF workflow execution.
///
/// ProcessAsync: single-agent workflow — entry executor handles AgentStepInput.
/// RunPipelineAsync: chained-executor pipeline with optional self-correction retry loop.
///   Self-correction: AF DAGs cannot have back-edges, so correction is external —
///   if the terminal output has RequiresCorrection=true the entire pipeline re-runs
///   with a correction hint in parameters, up to MaxCorrectionAttempts.
/// </summary>
public sealed class AgentFrameworkExecutionRuntime(
    DynamicWorkflowBuilder workflowBuilder,
    AgentRegistry agentRegistry,
    ILogger<AgentFrameworkExecutionRuntime> logger) : IAgentExecutionRuntime
{
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

        var workflow = workflowBuilder.BuildSingleAgent(agent, user);
        var input = new AgentStepInput(
            request.InputSource,
            request.OutputDestination,
            request.Parameters);

        var run = await InProcessExecution.RunAsync(workflow, input, null, ct);
        return WorkflowResultMapper.Map(run, agent.Name);
    }

    public async Task<ProcessingResult> RunPipelineAsync(
        PipelineRequest pipeline,
        UserContext user,
        CancellationToken ct = default)
    {
        logger.LogInformation(
            "[User: {UserId}] AF runtime: pipeline '{Name}' — {Count} agents, self-correction={Enabled}",
            user.UserId, pipeline.Name, pipeline.AgentIds.Count,
            pipeline.EnableSelfCorrection);

        // Load agent definitions for all pipeline steps.
        var agents = new List<Core.AgentDefinition>(pipeline.AgentIds.Count);
        foreach (var id in pipeline.AgentIds)
        {
            var agent = await agentRegistry.GetAgentByIdAsync(id, ct)
                ?? throw new InvalidOperationException($"Agent {id} not found in pipeline '{pipeline.Name}'.");
            agents.Add(agent);
        }

        var maxAttempts = pipeline.EnableSelfCorrection
            ? Math.Max(1, pipeline.MaxCorrectionAttempts)
            : 1;

        // Run with optional self-correction retry loop.
        // Since AF DAGs forbid back-edges, correction is an external retry:
        // re-run the entire pipeline with the mismatch details appended to parameters.
        IReadOnlyDictionary<string, string>? parameters = pipeline.Parameters;
        ProcessingResult? lastResult = null;

        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            var workflow = workflowBuilder.BuildPipeline(agents, user);
            var input = new AgentStepInput(
                pipeline.InputSource,
                pipeline.OutputDestination,
                parameters);

            var run = await InProcessExecution.RunAsync(workflow, input, null, ct);
            lastResult = WorkflowResultMapper.Map(run, pipeline.Name);

            if (lastResult.Warnings?.Contains("RequiresCorrection") != true)
                return lastResult;

            if (attempt + 1 >= maxAttempts)
            {
                logger.LogWarning(
                    "[User: {UserId}] Pipeline '{Name}' exhausted {Max} correction attempt(s)",
                    user.UserId, pipeline.Name, maxAttempts);
                return lastResult;
            }

            logger.LogInformation(
                "[User: {UserId}] Pipeline '{Name}' requires correction — attempt {Attempt}/{Max}",
                user.UserId, pipeline.Name, attempt + 1, maxAttempts);

            // Inject correction hint so agents can adapt on the next pass.
            var updated = parameters?.ToDictionary(kv => kv.Key, kv => kv.Value)
                ?? new Dictionary<string, string>();
            updated["_correctionHint"] = lastResult.Message;
            updated["_correctionAttempt"] = (attempt + 1).ToString();
            parameters = updated;
        }

        return lastResult ?? ProcessingResult.Fail("Pipeline produced no result.");
    }
}