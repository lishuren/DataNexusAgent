using System.Text.Json;

return await RunAsync(args);

static async Task<int> RunAsync(string[] args)
{
    var mode = args.FirstOrDefault() ?? "stream-success";
    var request = await ReadRequestAsync();

    switch (mode)
    {
        case "stream-success":
            await WriteEventAsync(new { type = "status", message = $"accepted:{request.AgentName}" });
            await WriteEventAsync(new { type = "chunk", text = $"input={request.Input}" });
            await WriteEventAsync(new { type = "chunk", text = $"|user={request.UserId}" });
            await WriteEventAsync(new { type = "result", success = true, message = $"done:{request.OutputDestination}" });
            return 0;

        case "result-data":
            await WriteEventAsync(new
            {
                type = "result",
                success = true,
                message = "with-data",
                data = new
                {
                    input = request.Input,
                    outputDestination = request.OutputDestination,
                    parameterCount = request.ParameterCount,
                },
            });
            return 0;

        case "missing-result":
            await WriteEventAsync(new { type = "status", message = "partial" });
            await WriteEventAsync(new { type = "chunk", text = "partial-data" });
            return 0;

        case "invalid-json":
            await Console.Out.WriteLineAsync("not-json");
            await Console.Out.FlushAsync();
            return 0;

        case "failure-result":
            await WriteEventAsync(new { type = "result", success = false, message = "fixture-failure" });
            return 0;

        default:
            await Console.Error.WriteLineAsync($"Unknown mode '{mode}'.");
            return 2;
    }
}

static async Task<RequestContext> ReadRequestAsync()
{
    var raw = await Console.In.ReadToEndAsync();
    using var doc = JsonDocument.Parse(raw);
    var root = doc.RootElement;

    return new RequestContext(
        ProtocolVersion: root.GetProperty("protocolVersion").GetInt32(),
        AgentId: root.GetProperty("agentId").GetInt32(),
        AgentName: root.GetProperty("agentName").GetString() ?? string.Empty,
        UserId: root.GetProperty("userId").GetString() ?? string.Empty,
        Input: root.GetProperty("input").GetString() ?? string.Empty,
        OutputDestination: root.GetProperty("outputDestination").GetString() ?? string.Empty,
        ParameterCount: root.TryGetProperty("parameters", out var parameters) && parameters.ValueKind == JsonValueKind.Object
            ? parameters.EnumerateObject().Count()
            : 0);
}

static async Task WriteEventAsync<T>(T payload)
{
    await Console.Out.WriteLineAsync(JsonSerializer.Serialize(payload));
    await Console.Out.FlushAsync();
}

internal sealed record RequestContext(
    int ProtocolVersion,
    int AgentId,
    string AgentName,
    string UserId,
    string Input,
    string OutputDestination,
    int ParameterCount);