namespace DataNexus.Agents;

/// <summary>
/// Well-known plugin names used in agent definitions.
/// Avoids magic strings scattered across the codebase.
/// </summary>
internal static class PluginNames
{
    internal const string InputProcessor = "InputProcessor";
    internal const string OutputIntegrator = "OutputIntegrator";
}

/// <summary>
/// Structured helpers for plugin error signaling within MAF middleware.
/// Plugin errors are surfaced as specially-prefixed text in <see cref="Microsoft.Agents.AI.AgentResponse"/>
/// messages so the self-correction loop in <see cref="DataNexusEngine"/> can detect and retry.
/// </summary>
internal static class PluginError
{
    internal const string Prefix = "[PLUGIN_ERROR]";

    internal static bool IsPluginError(string? text) =>
        text is not null && text.StartsWith(Prefix, StringComparison.Ordinal);

    internal static string Format(string message) => $"{Prefix} {message}";

    internal static string Format(string code, string message) => $"{Prefix} {code}: {message}";
}
