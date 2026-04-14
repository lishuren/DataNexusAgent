using System.Text.Json;
using DataNexus.Core;

namespace DataNexus.Plugins;

public sealed class OutputIntegratorPlugin(
    IHttpClientFactory httpClientFactory,
    ILogger<OutputIntegratorPlugin> logger) : IPlugin
{
    public string Name => "OutputIntegrator";

    public async Task<PluginResult> ExecuteAsync(PluginContext context, CancellationToken ct = default)
    {
        logger.LogInformation("[User: {UserId}] OutputIntegrator — validating and executing", context.UserId);

        try
        {
            var destinationSchema = context.DestinationSchema
                ?? GetMetadataValue(context.Metadata, "Schema", "schema");

            // Step 1: Validate against destination schema if provided
            if (destinationSchema is not null)
            {
                var validation = ValidateAgainstSchema(context.InputData, destinationSchema);
                if (!validation.IsValid)
                {
                    logger.LogWarning(
                        "[User: {UserId}] Schema mismatch: {Details}",
                        context.UserId, validation.ErrorMessage);

                    return new PluginResult(
                        false, context.InputData, "SCHEMA_MISMATCH", validation.ErrorMessage);
                }
            }

            // Step 2: Route to the appropriate output destination
            var destination = ResolveDestination(context.Metadata);

            return destination switch
            {
                "api" => await ExecuteApiCallAsync(context, ct),
                "database" => await ExecuteDatabaseWriteAsync(context, ct),
                "passthrough" => new PluginResult(true, context.InputData),
                _ => throw new NotSupportedException($"Unsupported destination: {destination}")
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[User: {UserId}] OutputIntegrator — failed", context.UserId);
            return new PluginResult(false, string.Empty, "EXECUTION_ERROR", ex.Message);
        }
    }

    private static SchemaValidationResult ValidateAgainstSchema(string data, string schema)
    {
        try
        {
            using var dataDoc = JsonDocument.Parse(data);
            using var schemaDoc = JsonDocument.Parse(schema);

            // Extract required fields from the schema object
            var requiredFields = schemaDoc.RootElement.EnumerateObject()
                .Select(p => p.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (dataDoc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in dataDoc.RootElement.EnumerateArray())
                {
                    foreach (var field in requiredFields)
                    {
                        if (!item.TryGetProperty(field, out _))
                        {
                            return new SchemaValidationResult(
                                false, $"Missing required field '{field}' in data row");
                        }
                    }
                }
            }
            else if (dataDoc.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var field in requiredFields)
                {
                    if (!dataDoc.RootElement.TryGetProperty(field, out _))
                    {
                        return new SchemaValidationResult(
                            false, $"Missing required field '{field}' in data");
                    }
                }
            }

            return new SchemaValidationResult(true, null);
        }
        catch (JsonException ex)
        {
            return new SchemaValidationResult(false, $"Invalid JSON: {ex.Message}");
        }
    }

    private async Task<PluginResult> ExecuteApiCallAsync(PluginContext context, CancellationToken ct)
    {
        var endpoint = ResolveApiEndpoint(context.Metadata)
            ?? throw new InvalidOperationException("ApiEndpoint not specified in metadata");

        // SSRF protection: only allow HTTPS endpoints
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps)
        {
            return new PluginResult(
                false, string.Empty, "INVALID_ENDPOINT", "Only HTTPS endpoints are permitted");
        }

        var client = httpClientFactory.CreateClient("DataNexusOutput");
        using var content = new StringContent(
            context.InputData, System.Text.Encoding.UTF8, "application/json");

        var response = await client.PostAsync(uri, content, ct);
        var responseBody = await response.Content.ReadAsStringAsync(ct);

        return response.IsSuccessStatusCode
            ? new PluginResult(true, responseBody)
            : new PluginResult(false, responseBody, "API_ERROR", $"API returned {response.StatusCode}");
    }

    private Task<PluginResult> ExecuteDatabaseWriteAsync(PluginContext context, CancellationToken ct)
    {
        // TODO: Implement database write support
        throw new NotImplementedException(
            "Database write destination is not yet implemented. Use 'api' destination instead.");
    }

    private static string ResolveDestination(IReadOnlyDictionary<string, string>? metadata)
    {
        var explicitDestination = GetMetadataValue(metadata, "Destination");
        if (!string.IsNullOrWhiteSpace(explicitDestination))
            return explicitDestination.Trim().ToLowerInvariant();

        if (ResolveApiEndpoint(metadata) is not null)
            return "api";

        var outputDestination = GetMetadataValue(metadata, "OutputDestination");
        if (string.Equals(outputDestination, "database", StringComparison.OrdinalIgnoreCase))
            return "database";

        return IsPassthroughOutput(outputDestination)
            ? "passthrough"
            : "api";
    }

    private static string? ResolveApiEndpoint(IReadOnlyDictionary<string, string>? metadata)
    {
        var endpoint = GetMetadataValue(metadata, "ApiEndpoint", "Endpoint", "endpoint");
        if (!string.IsNullOrWhiteSpace(endpoint))
            return endpoint;

        var outputDestination = GetMetadataValue(metadata, "OutputDestination");
        return IsHttpsUrl(outputDestination) ? outputDestination : null;
    }

    private static string? GetMetadataValue(
        IReadOnlyDictionary<string, string>? metadata,
        params string[] keys)
    {
        if (metadata is null)
            return null;

        foreach (var key in keys)
        {
            if (metadata.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
                return value;
        }

        foreach (var (key, value) in metadata)
        {
            if (string.IsNullOrWhiteSpace(value))
                continue;

            foreach (var candidate in keys)
            {
                if (string.Equals(key, candidate, StringComparison.OrdinalIgnoreCase))
                    return value;
            }
        }

        return null;
    }

    private static bool IsPassthroughOutput(string? outputDestination)
    {
        if (string.IsNullOrWhiteSpace(outputDestination))
            return false;

        return outputDestination.Trim().ToLowerInvariant() switch
        {
            "json" => true,
            "csv" => true,
            "sql" => true,
            "markdown" => true,
            "html" => true,
            "text" => true,
            "plain text" => true,
            "plaintext" => true,
            _ => false,
        };
    }

    private static bool IsHttpsUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps;

    private sealed record SchemaValidationResult(bool IsValid, string? ErrorMessage);
}
