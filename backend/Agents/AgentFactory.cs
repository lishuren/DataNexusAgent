using System.Runtime.CompilerServices;
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

public sealed class AgentExecutionTrace
{
    public bool InputPluginRan { get; set; }
    public string? ParsedInput { get; set; }
    public string? InputPluginErrorCode { get; set; }
    public string? InputPluginErrorMessage { get; set; }
    public string? RawLlmResponse { get; set; }
    public bool OutputPluginRan { get; set; }
    public string? OutputPluginResult { get; set; }
    public string? OutputPluginErrorCode { get; set; }
    public string? OutputPluginErrorMessage { get; set; }
}

public sealed record CreateRuntimeAgentResult(
    AIAgent Agent,
    string BuiltInstructions,
    IReadOnlyList<SkillDefinition> ResolvedSkills,
    AgentExecutionTrace Trace);

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
        var skills = agentDef.SkillNames.Count > 0
            ? await skillRegistry.GetSkillsForUserAsync(user.UserId, ct, [.. agentDef.SkillNames])
            : [];

        var instructions = agentDef.SystemPrompt;

        var contextProviders = skills.Count > 0
            ? BuildSkillContextProviders(skills)
            : null;

        var agent = new ChatClientAgent(
            chatClient,
            new ChatClientAgentOptions
            {
                Name = agentDef.Name,
                Description = agentDef.Description,
                ChatOptions = new ChatOptions
                {
                    Instructions = instructions,
                },
                AIContextProviders = contextProviders,
            },
            loggerFactory,
            services: null);

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

        return AttachPluginMiddleware(
            baseAgent,
            agentDef,
            user.UserId,
            inputPlugin,
            outputPlugin,
            parameters,
            trace: null);
    }

    /// <summary>
    /// Creates an AF runtime-ready LLM agent with plugin middleware embedded and execution trace capture.
    /// Used by single-agent execution so the engine can run everything through AF while still
    /// returning detailed debug information.
    /// </summary>
    public async Task<CreateRuntimeAgentResult> CreateRuntimeAgentAsync(
        AgentDefinition agentDef,
        UserContext user,
        InputProcessorPlugin inputPlugin,
        OutputIntegratorPlugin outputPlugin,
        IReadOnlyDictionary<string, string>? parameters,
        string? promptOverride = null,
        CancellationToken ct = default)
    {
        var effectiveDef = promptOverride is not null
            ? agentDef with { SystemPrompt = promptOverride }
            : agentDef;

        var baseResult = await CreateAgentAsync(effectiveDef, user, ct);
        var trace = new AgentExecutionTrace();

        var runtimeAgent = AttachPluginMiddleware(
            baseResult.Agent,
            effectiveDef,
            user.UserId,
            inputPlugin,
            outputPlugin,
            parameters,
            trace);

        return new CreateRuntimeAgentResult(
            runtimeAgent,
            baseResult.BuiltInstructions,
            baseResult.ResolvedSkills,
            trace);
    }

    private static AIAgent AttachPluginMiddleware(
        AIAgent baseAgent,
        AgentDefinition agentDef,
        string userId,
        InputProcessorPlugin inputPlugin,
        OutputIntegratorPlugin outputPlugin,
        IReadOnlyDictionary<string, string>? parameters,
        AgentExecutionTrace? trace)
    {
        var hasInput = agentDef.PluginNames.Contains(PluginNames.InputProcessor);
        var hasOutput = agentDef.PluginNames.Contains(PluginNames.OutputIntegrator);

        if (!hasInput && !hasOutput)
            return baseAgent;

        return baseAgent.AsBuilder()
            .Use(
                runFunc: async (messages, session, options, inner, cancellationToken) =>
                {
                    var (msgList, inputErrorResponse) = await ApplyInputPluginAsync(
                        messages,
                        hasInput,
                        inputPlugin,
                        userId,
                        parameters,
                        trace,
                        cancellationToken);

                    if (inputErrorResponse is not null)
                        return inputErrorResponse;

                    var response = await inner.RunAsync(msgList, session, options, cancellationToken);

                    if (trace is not null)
                        trace.RawLlmResponse = response.Text ?? string.Empty;

                    var outputErrorResponse = await ApplyOutputPluginAsync(
                        response.Text ?? string.Empty,
                        hasOutput,
                        outputPlugin,
                        userId,
                        parameters,
                        trace,
                        cancellationToken);

                    if (outputErrorResponse is not null)
                        return outputErrorResponse;

                    return response;
                },
                runStreamingFunc: (messages, session, options, inner, cancellationToken) =>
                    RunStreamingWithPluginsAsync(
                        messages,
                        session,
                        options,
                        inner,
                        hasInput,
                        hasOutput,
                        inputPlugin,
                        outputPlugin,
                        userId,
                        parameters,
                        trace,
                        cancellationToken))
            .Build();
    }

    private static async Task<(List<ChatMessage> Messages, AgentResponse? ErrorResponse)> ApplyInputPluginAsync(
        IEnumerable<ChatMessage> messages,
        bool hasInput,
        InputProcessorPlugin inputPlugin,
        string userId,
        IReadOnlyDictionary<string, string>? parameters,
        AgentExecutionTrace? trace,
        CancellationToken cancellationToken)
    {
        var msgList = messages.ToList();

        if (!hasInput)
            return (msgList, null);

        if (trace is not null)
            trace.InputPluginRan = true;

        var lastUser = msgList.LastOrDefault(m => m.Role == ChatRole.User);
        var inputText = lastUser?.Text ?? string.Empty;
        var paramDict = parameters?.ToDictionary(kv => kv.Key, kv => kv.Value);
        var ctx = new PluginContext(userId, inputText, Metadata: paramDict);
        var result = await inputPlugin.ExecuteAsync(ctx, cancellationToken);

        if (!result.Success)
        {
            if (trace is not null)
            {
                trace.InputPluginErrorCode = result.ErrorCode ?? "PARSE_ERROR";
                trace.InputPluginErrorMessage = result.ErrorMessage ?? "Unknown error";
            }

            return (msgList, CreatePluginErrorResponse(
                PluginError.Format($"Input: {result.ErrorMessage}")));
        }

        if (trace is not null)
            trace.ParsedInput = result.Output;

        if (lastUser is not null)
        {
            var idx = msgList.IndexOf(lastUser);
            msgList[idx] = new ChatMessage(ChatRole.User, result.Output);
        }

        return (msgList, null);
    }

    private static async Task<AgentResponse?> ApplyOutputPluginAsync(
        string responseText,
        bool hasOutput,
        OutputIntegratorPlugin outputPlugin,
        string userId,
        IReadOnlyDictionary<string, string>? parameters,
        AgentExecutionTrace? trace,
        CancellationToken cancellationToken)
    {
        if (!hasOutput)
            return null;

        if (trace is not null)
            trace.OutputPluginRan = true;

        var paramDict = parameters?.ToDictionary(kv => kv.Key, kv => kv.Value);
        var outCtx = new PluginContext(
            userId,
            responseText,
            paramDict?.GetValueOrDefault("Schema"),
            paramDict);
        var outResult = await outputPlugin.ExecuteAsync(outCtx, cancellationToken);

        if (trace is not null)
            trace.OutputPluginResult = outResult.Success ? outResult.Output : outResult.ErrorMessage;

        if (outResult.Success)
            return null;

        if (trace is not null)
        {
            trace.OutputPluginErrorCode = outResult.ErrorCode ?? "ERROR";
            trace.OutputPluginErrorMessage = outResult.ErrorMessage ?? "Unknown error";
        }

        return CreatePluginErrorResponse(
            PluginError.Format(outResult.ErrorCode ?? "ERROR", outResult.ErrorMessage ?? "Unknown error"));
    }

    private static async IAsyncEnumerable<AgentResponseUpdate> RunStreamingWithPluginsAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        AgentRunOptions? options,
        AIAgent inner,
        bool hasInput,
        bool hasOutput,
        InputProcessorPlugin inputPlugin,
        OutputIntegratorPlugin outputPlugin,
        string userId,
        IReadOnlyDictionary<string, string>? parameters,
        AgentExecutionTrace? trace,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var (msgList, inputErrorResponse) = await ApplyInputPluginAsync(
            messages,
            hasInput,
            inputPlugin,
            userId,
            parameters,
            trace,
            cancellationToken);

        if (inputErrorResponse is not null)
        {
            foreach (var update in inputErrorResponse.ToAgentResponseUpdates())
                yield return update;
            yield break;
        }

        var responseText = new StringBuilder();

        await foreach (var update in inner.RunStreamingAsync(msgList, session, options, cancellationToken)
            .WithCancellation(cancellationToken))
        {
            if (!string.IsNullOrEmpty(update.Text))
                responseText.Append(update.Text);

            yield return update;
        }

        if (trace is not null)
            trace.RawLlmResponse = responseText.ToString();

        var outputErrorResponse = await ApplyOutputPluginAsync(
            responseText.ToString(),
            hasOutput,
            outputPlugin,
            userId,
            parameters,
            trace,
            cancellationToken);

        if (outputErrorResponse is null)
            yield break;

        foreach (var update in outputErrorResponse.ToAgentResponseUpdates())
            yield return update;
    }

    private static AgentResponse CreatePluginErrorResponse(string text) =>
        new([new ChatMessage(ChatRole.Assistant, text)]);

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
        return (await CreateRuntimeAgentAsync(
            agentDef,
            user,
            inputPlugin,
            outputPlugin,
            parameters,
            promptOverride,
            ct)).Agent;
    }

    private IList<AIContextProvider>? BuildSkillContextProviders(IReadOnlyList<SkillDefinition> skills)
    {
        var skillDirectories = skills
            .Select(skill => skill.PackageDirectory)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (skillDirectories.Count == 0)
            return null;

#pragma warning disable MAAI001
        var provider = new FileAgentSkillsProvider(
            skillDirectories!,
            new FileAgentSkillsProviderOptions(),
            loggerFactory);
#pragma warning restore MAAI001

        return [provider];
    }
}
