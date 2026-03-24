namespace DataNexus.Models;

public sealed record ProcessingResult(
    bool Success,
    string Message,
    object? Data = null,
    IReadOnlyList<string>? Warnings = null)
{
    public static ProcessingResult Ok(string message, object? data = null, IReadOnlyList<string>? warnings = null) =>
        new(true, message, data, warnings);

    public static ProcessingResult Fail(string message) =>
        new(false, message);
}
