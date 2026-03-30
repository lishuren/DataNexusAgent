using System.Text;
using DataNexus.Core;
using DataNexus.Identity;
using DataNexus.Models;
using DataNexus.Plugins;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace DataNexus.Agents;

/// <summary>Result of <see cref="AgentFactory.CreateAgentAsync"/> including resolved skills and built instructions.</summary>
public sealed record CreateAgentResult(
    AIAgent Agent,
    string BuiltInstructions,
    IReadOnlyList<SkillDefinition> ResolvedSkills);

/// <summary>
/// Creates Microsoft Agent Framework <see cref="ChatClientAgent"/> instances from
/// database-stored <see cref="AgentDefinition"/> records.  Each agent is constructed
/// with skill-enriched instructions and wrapped with audit-logging middleware.
/// Also creates <see cref="ExternalAgentAdapter"/>-backed agents for CLI/script agents.
/// </summary>
public sealed class AgentFactory(
    IChatClient chatClient,
    SkillRegistry skillRegistry,
    ExternalProcessRunner externalRunner,
    ILoggerFactory loggerFactory)
{
    /// <summary>
    /// Builds a <see cref="AIAgent"/> for the given agent definition.
    /// Skills are resolved, instructions are assembled, and AF middleware is attached.
    /// </summary>
    public async Task<CreateAgentResult> CreateAgentAsync(
        AgentDefinition agentDef,
        UserContext user,
        CancellationToken ct = default)
    {
        // 1. Resolve skills for this agent & user
        var skills = agentDef.SkillNames.Count > 0
            ? await skillRegistry.GetSkillsForUserAsync(user.UserId, ct, [.. agentDef.SkillNames])
            : await skillRegistry.GetSkillsForUserAsync(user.UserId, ct);

        // 2. Build the full instructions (system prompt + skill text)
        var instructions = BuildInstructions(agentDef.SystemPrompt, skills);

        // 3. Create the ChatClientAgent (AF's IChatClient-backed AIAgent)
        var agent = new ChatClientAgent(
            chatClient,
            name: agentDef.Name,
            instructions: instructions);

        // 4. Wrap with audit-logging middleware
        var logger = loggerFactory.CreateLogger($"Agent.{agentDef.Name}");
        var userId = user.UserId;

        var builtAgent = agent.AsBuilder()
            .Use(
                runFunc: async (messages, session, options, innerAgent, cancellationToken) =>
                {
                    logger.LogInformation("[User: {UserId}] Agent '{Agent}' starting", userId, agentDef.Name);
                    var response = await innerAgent.RunAsync(messages, session, options, cancellationToken);
                    logger.LogInformation("[User: {UserId}] Agent '{Agent}' completed", userId, agentDef.Name);
                    return response;
                },
                runStreamingFunc: (messages, session, options, innerAgent, cancellationToken) =>
                {
                    logger.LogInformation("[User: {UserId}] Agent '{Agent}' streaming", userId, agentDef.Name);
                    return innerAgent.RunStreamingAsync(messages, session, options, cancellationToken);
                })
            .Build();

        return new CreateAgentResult(builtAgent, instructions, skills);
    }

    /// <summary>
    /// Builds the full instruction text by appending skill definitions to the agent's
    /// base system prompt.  This is the text that becomes the ChatClientAgent's instructions.
    /// </summary>
    internal static string BuildInstructions(string agentPrompt, IReadOnlyList<SkillDefinition> skills)
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

    /// <summary>
    /// Creates an AF agent for any <see cref="AgentDefinition"/>, routing LLM agents
    /// to <see cref="ChatClientAgent"/> and external agents to <see cref="ExternalAgentAdapter"/>.
    /// Used by pipeline execution where agent types are mixed.
    /// </summary>
    public async Task<AIAgent> CreateAnyAgentAsync(
        AgentDefinition agentDef,
        UserContext user,
        CancellationToken ct = default)
    {
        if (agentDef.ExecutionType == AgentExecutionType.External)
            return CreateExternalAgent(agentDef, user);

        return (await CreateAgentAsync(agentDef, user, ct)).Agent;
    }

    /// <summary>
    /// Wraps an external (CLI/script) agent as an AF <see cref="AIAgent"/>
    /// via <see cref="ExternalAgentAdapter"/>.
    /// </summary>
    public AIAgent CreateExternalAgent(AgentDefinition agentDef, UserContext user)
    {
        var logger = loggerFactory.CreateLogger($"Agent.{agentDef.Name}");
        return ExternalAgentAdapter.Create(chatClient, externalRunner, agentDef, user, logger);
    }

    /// <summary>
    /// Creates an AF agent with deterministic plugins embedded as middleware.
    /// Used for <see cref="AgentWorkflowBuilder.BuildSequential"/> pipelines where
    /// each agent must be self-contained (InputProcessor → LLM → OutputIntegrator).
    /// </summary>
    public async Task<AIAgent> CreatePipelineAgentAsync(
        AgentDefinition agentDef,
        UserContext user,
        InputProcessorPlugin inputPlugin,
        OutputIntegratorPlugin outputPlugin,
        IReadOnlyDictionary<string, string>? parameters,
        CancellationToken ct = default)
    {
        var baseAgent = await CreateAnyAgentAsync(agentDef, user, ct);

        var hasInput = agentDef.PluginNames.Contains(PluginNames.InputProcessor);
        var hasOutput = agentDef.PluginNames.Contains(PluginNames.OutputIntegrator);

        if (!hasInput && !hasOutput)
            return baseAgent;

        var userId = user.UserId;

        return baseAgent.AsBuilder()
            .Use(
                runFunc: async (messages, session, options, inner, cancellationToken) =>
                {
                    var msgList = messages.ToList();

                    // Pre-LLM: InputProcessor plugin
                    if (hasInput)
                    {
                        var lastUser = msgList.LastOrDefault(m => m.Role == ChatRole.User);
                        var inputText = lastUser?.Text ?? string.Empty;
                        var paramDict = parameters?.ToDictionary(kv => kv.Key, kv => kv.Value);
                        var ctx = new PluginContext(userId, inputText, Metadata: paramDict);
                        var result = await inputPlugin.ExecuteAsync(ctx, cancellationToken);
                        if (!result.Success)
                            return new AgentResponse([new ChatMessage(ChatRole.Assistant,
                            PluginError.Format($"Input: {result.ErrorMessage}"))]);
                        if (lastUser is not null)
                        {
                            var idx = msgList.IndexOf(lastUser);
                            msgList[idx] = new ChatMessage(ChatRole.User, result.Output);
                        }
                    }

                    var response = await inner.RunAsync(msgList, session, options, cancellationToken);

                    // Post-LLM: OutputIntegrator plugin
                    if (hasOutput)
                    {
                        var paramDict2 = parameters?.ToDictionary(kv => kv.Key, kv => kv.Value);
                        var outCtx = new PluginContext(
                            userId, response.Text ?? string.Empty,
                            paramDict2?.GetValueOrDefault("Schema"),
                            paramDict2);
                        var outResult = await outputPlugin.ExecuteAsync(outCtx, cancellationToken);
                        if (!outResult.Success)
                            return new AgentResponse([new ChatMessage(ChatRole.Assistant,
                                PluginError.Format(outResult.ErrorCode ?? "ERROR", outResult.ErrorMessage ?? "Unknown error"))]);
                    }

                    return response;
                },
                // Middleware transforms messages (InputProcessor/OutputIntegrator),
                // so pass null to let MAF auto-derive streaming via the non-streaming path.
                runStreamingFunc: null)
            .Build();
    }

    /// <summary>
    /// Creates an AF pipeline-ready agent for an orchestration step.
    /// Supports an optional prompt override that replaces the agent's stored system prompt
    /// while still resolving skills and embedding plugins as AF middleware.
    /// Used by <see cref="DataNexusEngine.RunOrchestrationAsync"/> to build agents from
    /// <see cref="OrchestrationStep"/> definitions.
    /// </summary>
    public async Task<AIAgent> CreateOrchestrationStepAgentAsync(
        AgentDefinition agentDef,
        UserContext user,
        InputProcessorPlugin inputPlugin,
        OutputIntegratorPlugin outputPlugin,
        string? promptOverride,
        IReadOnlyDictionary<string, string>? parameters,
        CancellationToken ct = default)
    {
        // Apply prompt override before building
        var effectiveDef = promptOverride is not null
            ? agentDef with { SystemPrompt = promptOverride }
            : agentDef;

        return await CreatePipelineAgentAsync(effectiveDef, user, inputPlugin, outputPlugin, parameters, ct);
    }
}
