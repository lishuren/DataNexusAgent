namespace DataNexus.Models;

public sealed record ProcessingStreamEvent(
    string Type,
    string? Message = null,
    string? Text = null,
    string? SourceId = null,
    ProcessingResult? Result = null)
{
    public static ProcessingStreamEvent Status(string message, string? sourceId = null) =>
        new("status", Message: message, SourceId: sourceId);

    public static ProcessingStreamEvent Chunk(string text, string? sourceId = null) =>
        new("chunk", Text: text, SourceId: sourceId);

    public static ProcessingStreamEvent ResultEvent(ProcessingResult result) =>
        new("result", Message: result.Message, Result: result);
}