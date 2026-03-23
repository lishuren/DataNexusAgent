using System.Text;
using Azure.AI.Inference;
using DataNexus.Core;
using DataNexus.Identity;
using DataNexus.Models;
using DataNexus.Plugins;
using Microsoft.Extensions.Options;

namespace DataNexus.Agents;

public sealed class DataNexusEngine(
    ChatCompletionsClient aiClient,
    SkillRegistry skillRegistry,
    AgentRegistry agentRegistry,
    InputProcessorPlugin inputPlugin,
    OutputIntegratorPlugin outputPlugin,
    ExternalProcessRunner externalRunner,
    IOptions<GitHubModelsConfig> modelsConfig,
    ILogger<DataNexusEngine> logger) : IAgentExecutionRuntime
{
    private readonly string _model = modelsConfig.Value.Model;
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
    /// Runs a multi-agent pipeline: output of each agent feeds into the next.
    /// </summary>
    public async Task<ProcessingResult> RunPipelineAsync(
        PipelineRequest pipeline,
        UserContext user,
        CancellationToken ct = default)
    {
        logger.LogInformation(
            "[User: {UserId}] Pipeline '{Name}' started with {Count} agents",
            user.UserId, pipeline.Name, pipeline.AgentIds.Count);

        var currentInput = pipeline.InputSource;
        var appliedAgents = new List<string>();
        int totalAttempts = 0;

        for (var step = 0; step < pipeline.AgentIds.Count; step++)
        {
            var agentDef = await agentRegistry.GetAgentByIdAsync(pipeline.AgentIds[step], ct)
                ?? throw new InvalidOperationException($"Agent {pipeline.AgentIds[step]} not found");

            logger.LogInformation(
                "[User: {UserId}] Pipeline step {Step}/{Total}: {Agent}",
                user.UserId, step + 1, pipeline.AgentIds.Count, agentDef.Name);

            var stepRequest = new ProcessingRequest(
                agentDef.Id, currentInput, pipeline.OutputDestination,
                Parameters: pipeline.Parameters);

            var result = await RunSingleAgentAsync(agentDef, stepRequest, user, ct);

            if (!result.Success)
            {
                // Self-correction: loop back to previous agent if enabled
                if (pipeline.EnableSelfCorrection && step > 0)
                {
                    var corrected = await TrySelfCorrectAsync(
                        pipeline, step, currentInput, result.Message, user, ct);

                    if (corrected is not null)
                    {
                        result = corrected.Value.Result;
                        totalAttempts += corrected.Value.Attempts;

                        if (result.Success)
                        {
                            currentInput = result.Data?.ToString() ?? result.Message;
                            appliedAgents.Add($"{agentDef.Name} (corrected)");
                            continue;
                        }
                    }
                }

                return ProcessingResult.Fail(
                    $"Pipeline failed at step {step + 1} ({agentDef.Name}): {result.Message}");
            }

            currentInput = result.Data?.ToString() ?? result.Message;
            appliedAgents.Add(agentDef.Name);
        }

        logger.LogInformation("[User: {UserId}] Pipeline '{Name}' completed", user.UserId, pipeline.Name);

        List<string> warnings = [];
        if (totalAttempts > 0)
            warnings.Add($"Required {totalAttempts} correction attempts");

        return ProcessingResult.Ok(
            $"Pipeline '{pipeline.Name}' completed successfully",
            new { Agents = appliedAgents, Steps = pipeline.AgentIds.Count },
            warnings.Count > 0 ? warnings : null);
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
        // 1. Resolve skills for this agent
        var skills = agent.SkillNames.Count > 0
            ? await skillRegistry.GetSkillsForUserAsync(user.UserId, ct, [.. agent.SkillNames])
            : request.SkillName is not null
                ? await skillRegistry.GetSkillsForUserAsync(user.UserId, ct, request.SkillName)
                : await skillRegistry.GetSkillsForUserAsync(user.UserId, ct);

        // 2. Run input plugin if agent uses it
        string inputData = request.InputSource;
        if (agent.PluginNames.Contains("InputProcessor"))
        {
            var pluginCtx = new PluginContext(user.UserId, request.InputSource, Metadata: request.Parameters);
            var pluginResult = await inputPlugin.ExecuteAsync(pluginCtx, ct);
            if (!pluginResult.Success)
                return ProcessingResult.Fail($"Input parsing failed: {pluginResult.ErrorMessage}");
            inputData = pluginResult.Output;
        }

        // 3. Build system prompt = agent prompt + skill instructions
        var systemPrompt = BuildSystemPrompt(agent.SystemPrompt, skills);

        // 4. Call LLM
        var llmResponse = await CallLlmAsync(systemPrompt, inputData, ct);

        // 5. Run output plugin if agent uses it
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

        var skillNames = string.Join(", ", skills.Select(s => s.Name));
        logger.LogInformation(
            "[User: {UserId}] Agent '{Agent}' completed. Skills: [{Skills}]",
            user.UserId, agent.Name, skillNames);

        return ProcessingResult.Ok(
            $"Agent '{agent.Name}' completed successfully",
            llmResponse);
    }

    /// <summary>The original two-agent relay for backward compatibility.</summary>
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

        // Run Analyst
        var analysisResult = await RunSingleAgentAsync(analyst, request, user, ct);
        if (!analysisResult.Success)
            return ProcessingResult.Fail($"Analysis failed: {analysisResult.Message}");

        // Run Executor
        var execRequest = request with { InputSource = analysisResult.Data?.ToString() ?? "" };
        var executionResult = await RunSingleAgentAsync(executor, execRequest, user, ct);

        // Self-correction loop
        var attempt = 1;
        while (!executionResult.Success && executionResult.Message.Contains("Schema mismatch") && attempt < MaxCorrectionAttempts)
        {
            attempt++;
            logger.LogInformation(
                "[User: {UserId}] Self-correction — attempt {Attempt}/{Max}",
                user.UserId, attempt, MaxCorrectionAttempts);

            analysisResult = await RunSingleAgentAsync(analyst, request, user, ct);
            if (!analysisResult.Success)
                return ProcessingResult.Fail($"Re-analysis failed on attempt {attempt}");

            execRequest = request with { InputSource = analysisResult.Data?.ToString() ?? "" };
            executionResult = await RunSingleAgentAsync(executor, execRequest, user, ct);
        }

        if (!executionResult.Success)
            return executionResult;

        List<string> warnings = [];
        if (attempt > 1) warnings.Add($"Required {attempt} attempts to resolve schema mismatches");

        return ProcessingResult.Ok(executionResult.Message, executionResult.Data, warnings.Count > 0 ? warnings : null);
    }

    private async Task<(ProcessingResult Result, int Attempts)?> TrySelfCorrectAsync(
        PipelineRequest pipeline,
        int failedStep,
        string lastInput,
        string errorMessage,
        UserContext user,
        CancellationToken ct)
    {
        var prevAgentDef = await agentRegistry.GetAgentByIdAsync(pipeline.AgentIds[failedStep - 1], ct);
        if (prevAgentDef is null) return null;

        for (var attempt = 1; attempt <= pipeline.MaxCorrectionAttempts; attempt++)
        {
            logger.LogInformation(
                "[User: {UserId}] Pipeline self-correction attempt {Attempt}: re-running {Agent}",
                user.UserId, attempt, prevAgentDef.Name);

            var retryRequest = new ProcessingRequest(
                prevAgentDef.Id, lastInput, pipeline.OutputDestination,
                Parameters: pipeline.Parameters);

            var result = await RunSingleAgentAsync(prevAgentDef, retryRequest, user, ct);
            if (result.Success) return (result, attempt);
        }

        return null;
    }

    private async Task<string> CallLlmAsync(string systemPrompt, string userInput, CancellationToken ct)
    {
        var options = new ChatCompletionsOptions
        {
            Messages =
            {
                new ChatRequestSystemMessage(systemPrompt),
                new ChatRequestUserMessage(userInput)
            },
            Model = _model
        };

        var response = await aiClient.CompleteAsync(options, ct);
        return response.Value.Content;
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
