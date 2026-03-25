using System.IO.Compression;
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
            // Decompress gzip data URLs from the frontend before any further processing.
            var inputData = DecompressIfGzipped(context.InputData);

            var inputType = context.Metadata?.GetValueOrDefault("InputType", "auto") ?? "auto";

            if (inputType == "auto")
                inputType = DetectInputType(inputData);

            var parsedData = inputType switch
            {
                "excel" => await ParseExcelAsync(inputData, ct),
                "json" => ParseJson(inputData),
                "csv" => ParseCsv(inputData),
                "text" => ParseText(inputData),
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

    /// <summary>
    /// Detects gzip-compressed data URLs from the frontend and decompresses them back to a
    /// standard data URL with the original MIME type.
    /// Format: <c>data:application/gzip;x-original-type={mime};base64,{gzipped}</c>
    /// </summary>
    private string DecompressIfGzipped(string input)
    {
        const string gzipPrefix = "data:application/gzip;x-original-type=";
        if (!input.StartsWith(gzipPrefix, StringComparison.OrdinalIgnoreCase))
            return input;

        // Parse: data:application/gzip;x-original-type={encodedMime};base64,{data}
        var afterPrefix = input.AsSpan(gzipPrefix.Length);
        var semicolonIdx = afterPrefix.IndexOf(';');
        if (semicolonIdx < 0)
            return input;

        var originalMimeEncoded = afterPrefix[..semicolonIdx].ToString();
        var originalMime = Uri.UnescapeDataString(originalMimeEncoded);

        var commaIdx = input.IndexOf(',', gzipPrefix.Length);
        if (commaIdx < 0)
            return input;

        var gzippedBytes = Convert.FromBase64String(input[(commaIdx + 1)..]);

        using var gzipStream = new GZipStream(new MemoryStream(gzippedBytes), CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzipStream.CopyTo(output);
        var decompressedBytes = output.ToArray();

        logger.LogInformation(
            "Decompressed gzip payload: {CompressedSize} → {OriginalSize} bytes (original MIME: {Mime})",
            gzippedBytes.Length, decompressedBytes.Length, originalMime);

        // Reconstruct as a standard data URL with the original MIME type
        return $"data:{originalMime};base64,{Convert.ToBase64String(decompressedBytes)}";
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

    private static string ParseText(string input)
    {
        // Decode base64 data URL to raw text, then wrap as JSON so the LLM gets clean content.
        if (input.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var commaIdx = input.IndexOf(',');
            if (commaIdx >= 0)
            {
                var bytes = Convert.FromBase64String(input[(commaIdx + 1)..]);
                var text = Encoding.UTF8.GetString(bytes);
                return JsonSerializer.Serialize(new { content = text }, JsonSerializerOptions.Web);
            }
        }
        // Already plain text
        return JsonSerializer.Serialize(new { content = input }, JsonSerializerOptions.Web);
    }

    private static string ParseJson(string input)
    {
        // Decode base64 data URL to raw JSON text
        if (input.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var commaIdx = input.IndexOf(',');
            if (commaIdx >= 0)
            {
                var bytes = Convert.FromBase64String(input[(commaIdx + 1)..]);
                input = Encoding.UTF8.GetString(bytes);
            }
        }

        using var doc = JsonDocument.Parse(input);
        return JsonSerializer.Serialize(doc.RootElement, JsonSerializerOptions.Web);
    }

    private static string ParseCsv(string input)
    {
        // Decode base64 data URL to raw CSV text
        if (input.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var commaIdx = input.IndexOf(',');
            if (commaIdx >= 0)
            {
                var bytes = Convert.FromBase64String(input[(commaIdx + 1)..]);
                input = Encoding.UTF8.GetString(bytes);
            }
        }

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
        // Base64 data URL from browser file upload — parse the MIME type to detect format.
        if (input.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var semicolonIdx = input.IndexOf(';');
            var mime = semicolonIdx > 5 ? input[5..semicolonIdx].ToLowerInvariant() : string.Empty;

            return mime switch
            {
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"
                    or "application/vnd.ms-excel"
                    or "application/x-excel"
                    or "application/x-msexcel" => "excel",
                "application/json" => "json",
                "text/csv" => "csv",
                "text/plain"
                    or "text/markdown"
                    or "text/xml"
                    or "application/xml" => "text",
                // Unknown binary (e.g. older .xls without proper MIME) — attempt Excel
                _ when !mime.StartsWith("text/") => "excel",
                // Unknown text type — pass through as text
                _ => "text"
            };
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
