using DataNexus.Identity;
using DataNexus.Models;

namespace DataNexus.Agents;

public sealed class DataNexusEngine(
    AnalystAgent analyst,
    ExecutorAgent executor,
    ILogger<DataNexusEngine> logger)
{
    private const int MaxCorrectionAttempts = 3;

    /// <summary>
    /// Orchestrates the multi-agent relay:
    ///   User Request → Analyst (parse) → Executor (validate + execute)
    ///                        ↑ schema mismatch ↓
    ///                        └─── correction ───┘
    /// </summary>
    public async Task<ProcessingResult> ProcessAsync(
        ProcessingRequest request,
        UserContext user,
        CancellationToken ct = default)
    {
        logger.LogInformation(
            "[User: {UserId}] Engine started: {Source} → {Destination}",
            user.UserId, request.InputSource, request.OutputDestination);

        // --- Agent 1: Analyst ---
        var analysisResult = await analyst.ProcessAsync(request, user, ct);

        if (!analysisResult.Success)
            return ProcessingResult.Fail($"Analysis failed: {analysisResult.ErrorMessage}");

        // --- Agent 2: Executor ---
        var executionResult = await executor.ExecuteAsync(
            analysisResult.ParsedData, request, user, ct);

        // --- Self-Correction Loop ---
        var attempt = 1;
        while (executionResult.RequiresCorrection && attempt < MaxCorrectionAttempts)
        {
            attempt++;
            logger.LogInformation(
                "[User: {UserId}] Self-correction loop — attempt {Attempt}/{Max}",
                user.UserId, attempt, MaxCorrectionAttempts);

            var correction = new CorrectionRequest(
                analysisResult.ParsedData,
                executionResult.MismatchDetails ?? "Unknown schema mismatch",
                executionResult.DestinationSchema ?? "{}",
                attempt);

            // Loop back to Analyst for re-parse
            analysisResult = await analyst.ReprocessAsync(correction, user, ct);

            if (!analysisResult.Success)
            {
                return ProcessingResult.Fail(
                    $"Re-analysis failed on attempt {attempt}: {analysisResult.ErrorMessage}");
            }

            // Re-submit to Executor
            executionResult = await executor.ExecuteAsync(
                analysisResult.ParsedData, request, user, ct);
        }

        if (executionResult.RequiresCorrection)
        {
            return ProcessingResult.Fail(
                $"Failed after {MaxCorrectionAttempts} correction attempts. " +
                $"Last mismatch: {executionResult.MismatchDetails}");
        }

        if (!executionResult.Success)
            return ProcessingResult.Fail(executionResult.Message);

        logger.LogInformation("[User: {UserId}] Engine completed successfully", user.UserId);

        List<string> warnings = [];
        if (attempt > 1)
            warnings.Add($"Required {attempt} attempts to resolve schema mismatches");

        return ProcessingResult.Ok(
            executionResult.Message,
            new { SkillsApplied = analysisResult.SkillsApplied, Attempts = attempt },
            warnings.Count > 0 ? warnings : null);
    }
}
