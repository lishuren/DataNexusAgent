using System.Text.Json;
using ClosedXML.Excel;
using DataNexus.Core;

namespace DataNexus.Plugins;

public sealed class InputProcessorPlugin(
    IHttpClientFactory httpClientFactory,
    ILogger<InputProcessorPlugin> logger) : IPlugin
{
    public string Name => "InputProcessor";

    public async Task<PluginResult> ExecuteAsync(PluginContext context, CancellationToken ct = default)
    {
        logger.LogInformation("[User: {UserId}] InputProcessor — parsing data", context.UserId);

        try
        {
            var inputType = context.Metadata?.GetValueOrDefault("InputType", "auto") ?? "auto";

            if (inputType == "auto")
                inputType = DetectInputType(context.InputData);

            var parsedData = inputType switch
            {
                "excel" => await ParseExcelAsync(context.InputData, ct),
                "json" => ParseJson(context.InputData),
                "csv" => ParseCsv(context.InputData),
                _ => throw new NotSupportedException($"Unsupported input type: {inputType}")
            };

            logger.LogInformation(
                "[User: {UserId}] InputProcessor — completed, {Size} chars produced",
                context.UserId, parsedData.Length);

            return new PluginResult(true, parsedData);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[User: {UserId}] InputProcessor — failed", context.UserId);
            return new PluginResult(false, string.Empty, "PARSE_ERROR", ex.Message);
        }
    }

    private async Task<string> ParseExcelAsync(string source, CancellationToken ct)
    {
        string filePath = source;

        // Download from URL if the source looks like one
        if (Uri.TryCreate(source, UriKind.Absolute, out var uri) &&
            uri.Scheme == Uri.UriSchemeHttps)
        {
            filePath = await DownloadToTempAsync(uri, ct);
        }

        try
        {
            using var workbook = new XLWorkbook(filePath);
            var worksheet = workbook.Worksheets.First();
            var rows = new List<Dictionary<string, string>>();

            var headers = worksheet.Row(1).CellsUsed()
                .Select(c => c.GetString())
                .ToList();

            foreach (var row in worksheet.RowsUsed().Skip(1))
            {
                var rowData = new Dictionary<string, string>();
                for (var i = 0; i < headers.Count; i++)
                    rowData[headers[i]] = row.Cell(i + 1).GetString();

                rows.Add(rowData);
            }

            return JsonSerializer.Serialize(rows, JsonSerializerOptions.Web);
        }
        finally
        {
            // Clean up temp file if we downloaded it
            if (filePath != source && File.Exists(filePath))
                File.Delete(filePath);
        }
    }

    private static string ParseJson(string input)
    {
        using var doc = JsonDocument.Parse(input);
        return JsonSerializer.Serialize(doc.RootElement, JsonSerializerOptions.Web);
    }

    private static string ParseCsv(string input)
    {
        var lines = input.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0) return "[]";

        var headers = lines[0].Split(',').Select(h => h.Trim()).ToList();
        var rows = new List<Dictionary<string, string>>();

        foreach (var line in lines.Skip(1))
        {
            var values = line.Split(',');
            var row = new Dictionary<string, string>();
            for (var i = 0; i < headers.Count && i < values.Length; i++)
                row[headers[i]] = values[i].Trim();

            rows.Add(row);
        }

        return JsonSerializer.Serialize(rows, JsonSerializerOptions.Web);
    }

    private async Task<string> DownloadToTempAsync(Uri uri, CancellationToken ct)
    {
        if (uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("Only HTTPS downloads are permitted");

        var client = httpClientFactory.CreateClient("DataNexusInput");
        var ext = Path.GetExtension(uri.AbsolutePath);
        var tempPath = Path.Combine(Path.GetTempPath(), $"datanexus_{Guid.NewGuid()}{ext}");

        using var response = await client.GetAsync(uri, ct);
        response.EnsureSuccessStatusCode();

        await using var fs = File.Create(tempPath);
        await response.Content.CopyToAsync(fs, ct);

        return tempPath;
    }

    private static string DetectInputType(string input)
    {
        if (input.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase) ||
            input.EndsWith(".xls", StringComparison.OrdinalIgnoreCase))
            return "excel";

        if (Uri.TryCreate(input, UriKind.Absolute, out var uri) &&
            uri.AbsolutePath.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
            return "excel";

        var trimmed = input.TrimStart();
        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
            return "json";

        return "csv";
    }
}
