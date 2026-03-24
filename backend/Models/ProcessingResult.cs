namespace DataNexus.Models;

/// <summary>
/// Optional debug information attached to a processing result.
/// Helps users diagnose issues with their prompts, skills, and agents.
/// </summary>
public sealed record ProcessingDebugInfo(
    string ParsedInputPreview,
    int ParsedInputLength,
    IReadOnlyList<string> SkillsUsed,
    string RawLlmResponse);

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
