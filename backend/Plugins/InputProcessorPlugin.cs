using System.Text;
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
        bool isTempFile = false;

        // Decode base64 data URL (e.g. "data:application/...;base64,<data>") uploaded from browser
        if (source.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var commaIdx = source.IndexOf(',');
            if (commaIdx < 0)
                throw new FormatException("Invalid base64 data URL: missing comma separator.");
            var bytes = Convert.FromBase64String(source[(commaIdx + 1)..]);
            filePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.xlsx");
            await File.WriteAllBytesAsync(filePath, bytes, ct);
            isTempFile = true;
        }
        // Download from HTTPS URL
        else if (Uri.TryCreate(source, UriKind.Absolute, out var uri) &&
            uri.Scheme == Uri.UriSchemeHttps)
        {
            filePath = await DownloadToTempAsync(uri, ct);
            isTempFile = true;
        }

        try
        {
            using var workbook = new XLWorkbook(filePath);

            // Parse ALL worksheets so the LLM sees the full workbook.
            // An invoice file often spreads data across multiple sheets.
            var output = new Dictionary<string, object>();

            foreach (var worksheet in workbook.Worksheets)
            {
                var row1Cells = worksheet.Row(1).CellsUsed().ToList();
                var row2Cells = worksheet.Row(2).CellsUsed().ToList();

                // Tabular: both rows have 3+ non-empty cells → treat row 1 as headers.
                // Label-value: invoices, forms → read each cell by its column address.
                var isTabular = row1Cells.Count >= 3 && row2Cells.Count >= 3;

                if (isTabular)
                {
                    var headers = row1Cells.Select(c => c.GetString()).ToList();
                    var rows = new List<Dictionary<string, string>>();
                    foreach (var row in worksheet.RowsUsed().Skip(1))
                    {
                        var rowData = new Dictionary<string, string>();
                        for (var i = 0; i < headers.Count; i++)
                            rowData[headers[i]] = row.Cell(i + 1).GetString();
                        rows.Add(rowData);
                    }
                    output[worksheet.Name] = rows;
                }
                else
                {
                    var rows = new List<Dictionary<string, string>>();
                    foreach (var row in worksheet.RowsUsed())
                    {
                        var rowData = new Dictionary<string, string>();
                        foreach (var cell in row.CellsUsed())
                            rowData[cell.Address.ColumnLetter] = cell.GetString();
                        rows.Add(rowData);
                    }
                    output[worksheet.Name] = rows;
                }
            }

            return JsonSerializer.Serialize(output, JsonSerializerOptions.Web);
        }
        finally
        {
            if (isTempFile && File.Exists(filePath))
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
        var lines = ParseCsvLines(input);
        if (lines.Count == 0) return "[]";

        var headers = lines[0];
        var rows = new List<Dictionary<string, string>>();

        foreach (var values in lines.Skip(1))
        {
            var row = new Dictionary<string, string>();
            for (var i = 0; i < headers.Count && i < values.Count; i++)
                row[headers[i]] = values[i];

            rows.Add(row);
        }

        return JsonSerializer.Serialize(rows, JsonSerializerOptions.Web);
    }

    /// <summary>
    /// RFC 4180-compliant CSV line parser. Handles quoted fields containing commas,
    /// newlines, and escaped double-quotes (<c>""</c>).
    /// </summary>
    private static List<List<string>> ParseCsvLines(string input)
    {
        var result = new List<List<string>>();
        var currentRow = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        var i = 0;

        while (i < input.Length)
        {
            var ch = input[i];

            if (inQuotes)
            {
                if (ch == '"')
                {
                    // Peek ahead: escaped quote ("") or end of quoted field
                    if (i + 1 < input.Length && input[i + 1] == '"')
                    {
                        field.Append('"');
                        i += 2;
                    }
                    else
                    {
                        inQuotes = false;
                        i++;
                    }
                }
                else
                {
                    field.Append(ch);
                    i++;
                }
            }
            else
            {
                if (ch == '"')
                {
                    inQuotes = true;
                    i++;
                }
                else if (ch == ',')
                {
                    currentRow.Add(field.ToString().Trim());
                    field.Clear();
                    i++;
                }
                else if (ch == '\r' || ch == '\n')
                {
                    currentRow.Add(field.ToString().Trim());
                    field.Clear();
                    if (currentRow.Any(f => f.Length > 0))
                        result.Add(currentRow);
                    currentRow = [];
                    // Skip \r\n pair
                    if (ch == '\r' && i + 1 < input.Length && input[i + 1] == '\n')
                        i += 2;
                    else
                        i++;
                }
                else
                {
                    field.Append(ch);
                    i++;
                }
            }
        }

        // Handle last field / last row
        currentRow.Add(field.ToString().Trim());
        if (currentRow.Any(f => f.Length > 0))
            result.Add(currentRow);

        return result;
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
        // Base64 data URL from browser file upload
        if (input.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return input.Contains("spreadsheet") || input.Contains("excel") || input.Contains("xlsx")
                ? "excel"
                : "excel"; // treat all uploaded binary files as Excel by default
        }

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
