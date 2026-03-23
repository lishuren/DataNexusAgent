using DataNexus.Core;
using DataNexus.Identity;
using DataNexus.Plugins;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using System.Text;

namespace DataNexus.Agents.Af;

/// <summary>
/// AF workflow executor for LLM-based agents.
/// Handles both AgentStepInput (entry step) and AgentStepOutput (chained pipeline step).
/// ConfigureProtocol registers both handlers explicitly via RouteBuilder.
/// Execution order for each step:
///   1. Resolve skills from SkillRegistry
///   2. Run InputProcessorPlugin (deterministic pre-processing, not an AF tool)
///   3. Build skill-enriched system prompt
///   4. Create ChatClientAgent and run via AF inference
///   5. Run OutputIntegratorPlugin (controlled post-processing, not an AF tool)
///   6. Store step output in AF shared state for observability
/// </summary>
internal sealed partial class LlmAgentExecutor : Executor
{
    private readonly AgentDefinition _agent;
    private readonly UserContext _user;
    private readonly SkillRegistry _skillRegistry;
    private readonly InputProcessorPlugin _inputPlugin;
    private readonly OutputIntegratorPlugin _outputPlugin;
    private readonly IChatClient _chatClient;
    private readonly ILogger _logger;

    public LlmAgentExecutor(
        AgentDefinition agent,
        UserContext user,
        SkillRegistry skillRegistry,
        InputProcessorPlugin inputPlugin,
        OutputIntegratorPlugin outputPlugin,
        IChatClient chatClient,
        ILogger logger) : base($"LlmAgent_{agent.Id}_{agent.Name}")
    {
        _agent = agent;
        _user = user;
        _skillRegistry = skillRegistry;
        _inputPlugin = inputPlugin;
        _outputPlugin = outputPlugin;
        _chatClient = chatClient;
        _logger = logger;
    }

    // ConfigureProtocol registers message handlers so the workflow engine knows
    // how to dispatch AgentStepInput (initial) and AgentStepOutput (chained) messages.
    protected override ProtocolBuilder ConfigureProtocol(ProtocolBuilder builder)
    {
        // Declare the message type this executor will send to connected executors.
        builder.SendsMessage<AgentStepOutput>();
        // Declare the output type yielded to the workflow consumer (WorkflowOutputEvent).
        builder.YieldsOutput<AgentStepOutput>();

        // Register handlers for each accepted input type.
        // send=true → the return value is forwarded to the next executor via edges.
        builder.RouteBuilder.AddHandler<AgentStepInput, AgentStepOutput>(
            (input, ctx, ct) => HandleInitialAsync(input, ctx, ct), true);
        builder.RouteBuilder.AddHandler<AgentStepOutput, AgentStepOutput>(
            (prev, ctx, ct) => HandleChainedAsync(prev, ctx, ct), true);

        return builder;
    }

    // Entry handler: first step in a single-agent run or the head of a pipeline.
    [MessageHandler]
    private async ValueTask<AgentStepOutput> HandleInitialAsync(
        AgentStepInput input,
        IWorkflowContext context,
        CancellationToken ct = default) =>
        await RunCoreAsync(input.Data, input.Parameters, context, ct);

    // Chained handler: subsequent steps in a pipeline receive the previous step's output.
    [MessageHandler]
    private async ValueTask<AgentStepOutput> HandleChainedAsync(
        AgentStepOutput prev,
        IWorkflowContext context,
        CancellationToken ct = default)
    {
        // Conditional edges only forward successes, but guard here defensively.
        if (!prev.Success)
            return prev;

        return await RunCoreAsync(prev.Data ?? prev.Message, null, context, ct);
    }

    private async ValueTask<AgentStepOutput> RunCoreAsync(
        string inputData,
        IReadOnlyDictionary<string, string>? parameters,
        IWorkflowContext context,
        CancellationToken ct)
    {
        try
        {
            // 1. Resolve skills
            var skills = _agent.SkillNames.Count > 0
                ? await _skillRegistry.GetSkillsForUserAsync(_user.UserId, ct, [.. _agent.SkillNames])
                : await _skillRegistry.GetSkillsForUserAsync(_user.UserId, ct);

            // 2. Run InputProcessor plugin (pre-LLM, deterministic — not an AF tool)
            var processedInput = inputData;
            if (_agent.PluginNames.Contains("InputProcessor"))
            {
                var pluginCtx = new PluginContext(_user.UserId, inputData, Metadata: parameters);
                var pluginResult = await _inputPlugin.ExecuteAsync(pluginCtx, ct);
                if (!pluginResult.Success)
                    return new AgentStepOutput(false, $"Input parsing failed: {pluginResult.ErrorMessage}");
                processedInput = pluginResult.Output;
            }

            // 3. Build skill-enriched system prompt
            var systemPrompt = BuildSystemPrompt(_agent.SystemPrompt, skills);

            // 4. Create AF ChatClientAgent and run
            var afAgent = new ChatClientAgent(_chatClient, instructions: systemPrompt);
            var response = await afAgent.RunAsync(processedInput, cancellationToken: ct);
            var llmOutput = response.Text;

            // 5. Run OutputIntegrator plugin (post-LLM, controlled — not an AF tool)
            if (_agent.PluginNames.Contains("OutputIntegrator"))
            {
                var outCtx = new PluginContext(
                    _user.UserId, llmOutput,
                    parameters?.GetValueOrDefault("Schema"),
                    parameters);
                var outResult = await _outputPlugin.ExecuteAsync(outCtx, ct);

                if (!outResult.Success && outResult.ErrorCode == "SCHEMA_MISMATCH")
                    return new AgentStepOutput(
                        false,
                        $"Schema mismatch: {outResult.ErrorMessage}",
                        RequiresCorrection: true,
                        MismatchDetails: outResult.ErrorMessage);

                if (!outResult.Success)
                    return new AgentStepOutput(false, $"Output integration failed: {outResult.ErrorMessage}");
            }

            // 6. Store in AF shared state for observability
            await context.QueueStateUpdateAsync($"agent_{_agent.Id}_output", llmOutput);

            var skillNames = string.Join(", ", skills.Select(s => s.Name));
            _logger.LogInformation(
                "[User: {UserId}] AF Agent '{Agent}' completed. Skills: [{Skills}]",
                _user.UserId, _agent.Name, skillNames);

            return new AgentStepOutput(true, $"Agent '{_agent.Name}' completed successfully", llmOutput);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[User: {UserId}] AF Agent '{Agent}' failed", _user.UserId, _agent.Name);
            return new AgentStepOutput(false, $"Agent '{_agent.Name}' failed: {ex.Message}");
        }
    }

    private static string BuildSystemPrompt(string agentPrompt, IReadOnlyList<SkillDefinition> skills)
    {
        if (skills.Count == 0) return agentPrompt;

        var sb = new StringBuilder(agentPrompt);
        sb.AppendLine().AppendLine("## Loaded Skills");
        foreach (var skill in skills)
        {
            sb.AppendLine($"### {skill.Name} ({skill.Scope})");
            sb.AppendLine(skill.Instructions);
            sb.AppendLine();
        }
        return sb.ToString();
    }
}
