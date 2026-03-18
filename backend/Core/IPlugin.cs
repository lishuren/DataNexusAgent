namespace DataNexus.Core;

public interface IPlugin
{
    string Name { get; }
    Task<PluginResult> ExecuteAsync(PluginContext context, CancellationToken ct = default);
}

public sealed record PluginContext(
    string UserId,
    string InputData,
    string? DestinationSchema = null,
    IReadOnlyDictionary<string, string>? Metadata = null);

public sealed record PluginResult(
    bool Success,
    string Output,
    string? ErrorCode = null,
    string? ErrorMessage = null);
