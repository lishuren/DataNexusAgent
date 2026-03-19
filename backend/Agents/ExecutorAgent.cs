using System.Text;
using Azure.AI.Inference;
using DataNexus.Core;
using DataNexus.Identity;
using DataNexus.Models;
using DataNexus.Plugins;
using Microsoft.Extensions.Options;

namespace DataNexus.Agents;

public sealed class ExecutorAgent(
    ChatCompletionsClient aiClient,
    OutputIntegratorPlugin outputPlugin,
    SkillRegistry skillRegistry,
    IOptions<GitHubModelsConfig> modelsConfig,
    ILogger<ExecutorAgent> logger)
{
    private readonly string _model = modelsConfig.Value.Model;

    private const string BaseSystemPrompt = """
        You are the DataNexus Executor Agent. Your role is to:
        1. Validate processed data against destination schemas.
        2. Ensure data integrity before writing to APIs or databases.
        3. Detect schema mismatches and report them clearly.
        4. Execute output operations (API calls, DB writes).

        If a schema mismatch is detected, respond ONLY with JSON:
        { "requiresCorrection": true, "mismatchDetails": "description of the issue" }

        Otherwise, respond with:
        { "requiresCorrection": false, "summary": "execution summary" }
        """;

    public async Task<ExecutionResult> ExecuteAsync(
        string processedData,
        ProcessingRequest request,
        UserContext user,
        CancellationToken ct = default)
    {
        logger.LogInformation(
            "[User: {UserId}] Executor — validating for '{Destination}'",
            user.UserId, request.OutputDestination);

        // 1. Public skill rules apply to all validation
        var skills = await skillRegistry.GetPublicSkillsAsync(ct);
        var systemPrompt = BuildSystemPrompt(skills);

        // 2. AI-assisted schema validation
        var options = new ChatCompletionsOptions
        {
            Messages =
            {
                new ChatRequestSystemMessage(systemPrompt),
                new ChatRequestUserMessage($"""
                    Validate the following processed data for output to '{request.OutputDestination}':

                    {processedData}

                    Check for schema compliance, data integrity, and any skill-based rules.
                    """)
            },
            Model = _model
        };

        var aiResponse = await aiClient.CompleteAsync(options, ct);
        var validationOutput = aiResponse.Value.Content;

        // 3. Detect correction requests from the AI response
        if (validationOutput.Contains("\"requiresCorrection\": true", StringComparison.OrdinalIgnoreCase) ||
            validationOutput.Contains("\"requiresCorrection\":true", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning(
                "[User: {UserId}] Executor — schema mismatch detected, requesting correction",
                user.UserId);

            return new ExecutionResult(
                false,
                "Schema mismatch detected by AI validation",
                RequiresCorrection: true,
                MismatchDetails: validationOutput,
                DestinationSchema: request.Parameters?.GetValueOrDefault("Schema"));
        }

        // 4. Execute via the output plugin
        var pluginContext = new PluginContext(
            user.UserId,
            processedData,
            request.Parameters?.GetValueOrDefault("Schema"),
            request.Parameters);

        var pluginResult = await outputPlugin.ExecuteAsync(pluginContext, ct);

        if (!pluginResult.Success && pluginResult.ErrorCode == "SCHEMA_MISMATCH")
        {
            logger.LogWarning(
                "[User: {UserId}] Executor — plugin-level schema mismatch: {Error}",
                user.UserId, pluginResult.ErrorMessage);

            return new ExecutionResult(
                false,
                "Schema mismatch at plugin execution",
                RequiresCorrection: true,
                MismatchDetails: pluginResult.ErrorMessage,
                DestinationSchema: request.Parameters?.GetValueOrDefault("Schema"));
        }

        if (!pluginResult.Success)
            return new ExecutionResult(false, $"Execution failed: {pluginResult.ErrorMessage}");

        logger.LogInformation("[User: {UserId}] Executor — completed successfully", user.UserId);
        return new ExecutionResult(true, "Execution completed successfully");
    }

    private static string BuildSystemPrompt(IReadOnlyList<SkillDefinition> skills)
    {
        if (skills.Count == 0)
            return BaseSystemPrompt;

        var sb = new StringBuilder(BaseSystemPrompt);
        sb.AppendLine().AppendLine("## Public Skill Rules");

        foreach (var skill in skills)
        {
            sb.AppendLine($"### {skill.Name}");
            sb.AppendLine(skill.Instructions);
            sb.AppendLine();
        }

        return sb.ToString();
    }
}
