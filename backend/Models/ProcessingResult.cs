namespace DataNexus.Models;

/// <summary>
/// Per-step trace entry in the debug output.
/// </summary>
public sealed record DebugStep(
    string Step,
    string Status,
    string? Preview = null,
    int? Chars = null);

/// <summary>
/// Optional debug information attached to a processing result.
/// Helps users diagnose issues with their prompts, skills, and agents.
/// </summary>
public sealed record ProcessingDebugInfo(
    // Input plugin
    bool InputPluginRan,
    string ParsedInputPreview,
    int ParsedInputLength,
    // Skills
    IReadOnlyList<string> SkillsUsed,
    IReadOnlyList<DebugStep> SkillDetails,
    // System prompt sent to LLM
    string SystemPromptPreview,
    int SystemPromptLength,
    // LLM
    string RawLlmResponse,
    // Output plugin
    bool OutputPluginRan,
    string? OutputPluginResult);

public sealed record ProcessingResult(
    bool Success,
    string Message,
    object? Data = null,
    IReadOnlyList<string>? Warnings = null,
    ProcessingDebugInfo? Debug = null)
{
    public static ProcessingResult Ok(
        string message,
        object? data = null,
        IReadOnlyList<string>? warnings = null,
        ProcessingDebugInfo? debug = null) =>
        new(true, message, data, warnings, debug);

    public static ProcessingResult Fail(string message) =>
        new(false, message);
}
