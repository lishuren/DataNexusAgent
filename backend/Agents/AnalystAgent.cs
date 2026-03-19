using System.Text;
using Azure.AI.Inference;
using DataNexus.Core;
using DataNexus.Identity;
using DataNexus.Models;
using DataNexus.Plugins;
using Microsoft.Extensions.Options;

namespace DataNexus.Agents;

public sealed class AnalystAgent(
    ChatCompletionsClient aiClient,
    InputProcessorPlugin inputPlugin,
    SkillRegistry skillRegistry,
    IOptions<GitHubModelsConfig> modelsConfig,
    ILogger<AnalystAgent> logger)
{
    private readonly string _model = modelsConfig.Value.Model;

    private const string BaseSystemPrompt = """
        You are the DataNexus Analyst Agent. Your role is to:
        1. Parse and understand incoming data (Excel, JSON, CSV).
        2. Apply transformation rules from loaded Skills.
        3. Clean and normalize data for downstream processing.
        4. Output structured JSON ready for the Executor Agent.

        Always respond with valid JSON. If data cannot be parsed, explain the error in a JSON
        object: { "error": "description" }.
        """;

    public async Task<AnalysisResult> ProcessAsync(
        ProcessingRequest request,
        UserContext user,
        CancellationToken ct = default)
    {
        logger.LogInformation(
            "[User: {UserId}] Analyst — starting analysis of '{Source}'",
            user.UserId, request.InputSource);

        // 1. Resolve relevant skills
        var skills = request.SkillName is not null
            ? await skillRegistry.GetSkillsForUserAsync(user.UserId, ct, request.SkillName)
            : await skillRegistry.GetSkillsForUserAsync(user.UserId, ct);

        // 2. Parse input via plugin
        var pluginContext = new PluginContext(
            user.UserId,
            request.InputSource,
            Metadata: request.Parameters);

        var parseResult = await inputPlugin.ExecuteAsync(pluginContext, ct);
        if (!parseResult.Success)
        {
            return new AnalysisResult(
                false, string.Empty,
                ErrorMessage: $"Input parsing failed: {parseResult.ErrorMessage}");
        }

        // 3. AI-driven transformation with skill instructions injected
        var systemPrompt = BuildSystemPrompt(skills);

        var options = new ChatCompletionsOptions
        {
            Messages =
            {
                new ChatRequestSystemMessage(systemPrompt),
                new ChatRequestUserMessage(
                    $"Parse and transform the following data according to loaded skills:\n\n{parseResult.Output}")
            },
            Model = _model
        };

        var response = await aiClient.CompleteAsync(options, ct);
        var analysisOutput = response.Value.Content;

        var appliedSkills = string.Join(", ", skills.Select(s => s.Name));
        logger.LogInformation(
            "[User: {UserId}] Analyst — completed. Skills applied: [{Skills}]",
            user.UserId, appliedSkills);

        return new AnalysisResult(true, analysisOutput, appliedSkills);
    }

    public async Task<AnalysisResult> ReprocessAsync(
        CorrectionRequest correction,
        UserContext user,
        CancellationToken ct = default)
    {
        logger.LogInformation(
            "[User: {UserId}] Analyst — re-processing (attempt {Attempt}): {Details}",
            user.UserId, correction.AttemptNumber, correction.MismatchDetails);

        var skills = await skillRegistry.GetSkillsForUserAsync(user.UserId, ct);
        var systemPrompt = BuildSystemPrompt(skills);

        var options = new ChatCompletionsOptions
        {
            Messages =
            {
                new ChatRequestSystemMessage(systemPrompt),
                new ChatRequestUserMessage($"""
                    The previous analysis was rejected due to a schema mismatch.

                    Mismatch details: {correction.MismatchDetails}
                    Expected schema:  {correction.DestinationSchema}

                    Re-parse and transform the data to match the expected schema:

                    {correction.OriginalInput}
                    """)
            },
            Model = _model
        };

        var response = await aiClient.CompleteAsync(options, ct);
        var analysisOutput = response.Value.Content;

        return new AnalysisResult(
            true, analysisOutput,
            string.Join(", ", skills.Select(s => s.Name)));
    }

    private static string BuildSystemPrompt(IReadOnlyList<SkillDefinition> skills)
    {
        if (skills.Count == 0)
            return BaseSystemPrompt;

        var sb = new StringBuilder(BaseSystemPrompt);
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
