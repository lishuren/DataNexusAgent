using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using DataNexus.Core;
using DataNexus.Identity;
using DataNexus.Models;
using Microsoft.Extensions.Options;

namespace DataNexus.Agents;

/// <summary>
/// Executes external (CLI / script) agents as child processes.
///
/// Protocol:
///   • stdin  — receives a JSON object describing the request context.
///   • stdout — emits UTF-8 NDJSON events, one JSON object per line.
///   • stderr — captured for diagnostics on failure.
///   • Exit code 0 = success, non-zero = failure.
///
/// Security controls:
///   • Command must appear in the configured allowlist.
///   • Working directory must fall under an allowed prefix.
///   • Timeout is clamped to the configured maximum.
///   • No shell invocation — arguments are passed as a flat list.
/// </summary>
public sealed class ExternalProcessRunner(
    IOptions<ExternalAgentOptions> options,
    ILogger<ExternalProcessRunner> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ExternalAgentOptions _opts = options.Value;

    public async Task<ProcessingResult> RunAsync(
        AgentDefinition agent,
        ProcessingRequest request,
        UserContext user,
        CancellationToken ct)
    {
        var transcript = new StringBuilder();
        ProcessingResult? finalResult = null;

        await foreach (var streamEvent in RunStreamingAsync(agent, request, user, ct))
        {
            if (!string.IsNullOrWhiteSpace(streamEvent.Text))
                transcript.Append(streamEvent.Text);

            if (streamEvent.Result is not null)
                finalResult = streamEvent.Result;
        }

        if (finalResult is not null)
        {
            if (finalResult.Success && finalResult.Data is null && transcript.Length > 0)
                return finalResult with { Data = transcript.ToString() };

            return finalResult;
        }

        if (transcript.Length > 0)
            return ProcessingResult.Ok($"External agent '{agent.Name}' completed.", transcript.ToString());

        return ProcessingResult.Fail($"External agent '{agent.Name}' ended without a result event.");
    }

    public async IAsyncEnumerable<ProcessingStreamEvent> RunStreamingAsync(
        AgentDefinition agent,
        ProcessingRequest request,
        UserContext user,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var channel = Channel.CreateUnbounded<ProcessingStreamEvent>();

        _ = Task.Run(
            () => ProduceStreamEventsAsync(channel.Writer, agent, request, user, ct),
            CancellationToken.None);

        await foreach (var streamEvent in channel.Reader.ReadAllAsync(ct))
            yield return streamEvent;
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private bool IsCommandAllowed(string command)
    {
        var name = Path.GetFileName(command);
        return _opts.AllowedCommands.Exists(allowed =>
            string.Equals(allowed, command, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(allowed, name, StringComparison.OrdinalIgnoreCase));
    }

    private string? ResolveWorkingDirectory(string? requested)
    {
        if (string.IsNullOrWhiteSpace(requested))
            return null;

        var full = Path.GetFullPath(requested);

        if (_opts.AllowedWorkingDirectories.Count == 0)
            return null; // no allowed dirs → inherit backend cwd

        if (_opts.AllowedWorkingDirectories.Exists(prefix =>
                full.StartsWith(Path.GetFullPath(prefix), StringComparison.OrdinalIgnoreCase)))
            return full;

        logger.LogWarning("Working directory '{Dir}' not under any allowed prefix — ignoring", requested);
        return null;
    }

    private static IEnumerable<string> SplitArguments(string? args)
    {
        if (string.IsNullOrWhiteSpace(args))
            yield break;

        // Simple space-delimited split; quoted tokens are respected
        var sb = new StringBuilder();
        var inQuote = false;

        foreach (var ch in args)
        {
            if (ch == '"')
            {
                inQuote = !inQuote;
                continue;
            }

            if (ch == ' ' && !inQuote)
            {
                if (sb.Length > 0)
                {
                    yield return sb.ToString();
                    sb.Clear();
                }
                continue;
            }

            sb.Append(ch);
        }

        if (sb.Length > 0)
            yield return sb.ToString();
    }

    private async Task ProduceStreamEventsAsync(
        ChannelWriter<ProcessingStreamEvent> writer,
        AgentDefinition agent,
        ProcessingRequest request,
        UserContext user,
        CancellationToken ct)
    {
        Process? process = null;
        CancellationTokenSource? cts = null;

        try
        {
            if (!_opts.Enabled)
            {
                WriteEvent(writer, ProcessingStreamEvent.ResultEvent(
                    ProcessingResult.Fail("External agent execution is disabled by configuration.")));
                return;
            }

            var command = agent.Command
                ?? throw new InvalidOperationException($"External agent '{agent.Name}' has no Command configured.");

            if (!IsCommandAllowed(command))
            {
                logger.LogWarning(
                    "[User: {UserId}] Blocked external agent '{Agent}': command '{Command}' not in allowlist",
                    user.UserId,
                    agent.Name,
                    command);
                WriteEvent(writer, ProcessingStreamEvent.ResultEvent(
                    ProcessingResult.Fail($"Command '{command}' is not permitted.")));
                return;
            }

            var workDir = ResolveWorkingDirectory(agent.WorkingDirectory);
            var timeout = Math.Clamp(agent.TimeoutSeconds, 1, _opts.MaxTimeoutSeconds);

            var psi = new ProcessStartInfo
            {
                FileName = command,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            if (workDir is not null)
                psi.WorkingDirectory = workDir;

            foreach (var arg in SplitArguments(agent.Arguments))
                psi.ArgumentList.Add(arg);

            var payload = JsonSerializer.Serialize(new ExternalAgentRequestPayload(
                ProtocolVersion: 2,
                AgentId: agent.Id,
                AgentName: agent.Name,
                UserId: user.UserId,
                Input: request.InputSource,
                OutputDestination: request.OutputDestination,
                Parameters: request.Parameters), JsonOptions);

            logger.LogInformation(
                "[User: {UserId}] Starting external agent '{Agent}': {Command} (timeout {Timeout}s)",
                user.UserId,
                agent.Name,
                command,
                timeout);

            cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(timeout));

            process = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start external process.");

            try
            {
                await process.StandardInput.WriteAsync(payload);
            }
            finally
            {
                process.StandardInput.Close();
            }

            WriteEvent(writer, ProcessingStreamEvent.Status(
                $"External agent '{agent.Name}' launched using streamed NDJSON protocol.",
                agent.Name));

            var stderrTask = process.StandardError.ReadToEndAsync(cts.Token);
            ProcessingResult? finalResult = null;

            await foreach (var protocolEvent in ReadProtocolEventsAsync(
                process.StandardOutput,
                agent.Name,
                cts.Token))
            {
                switch (protocolEvent.Type)
                {
                    case ExternalAgentProtocolEventType.Status when !string.IsNullOrWhiteSpace(protocolEvent.Message):
                        WriteEvent(writer, ProcessingStreamEvent.Status(protocolEvent.Message, agent.Name));
                        break;

                    case ExternalAgentProtocolEventType.Chunk when !string.IsNullOrWhiteSpace(protocolEvent.Text):
                        WriteEvent(writer, ProcessingStreamEvent.Chunk(protocolEvent.Text, agent.Name));
                        break;

                    case ExternalAgentProtocolEventType.Result:
                        finalResult = protocolEvent.Success == false
                            ? ProcessingResult.Fail(protocolEvent.Message ?? $"External agent '{agent.Name}' returned failure.")
                            : ProcessingResult.Ok(
                                protocolEvent.Message ?? $"External agent '{agent.Name}' completed.",
                                ConvertProtocolData(protocolEvent.Data));
                        WriteEvent(writer, ProcessingStreamEvent.ResultEvent(finalResult));
                        break;
                }
            }

            await process.WaitForExitAsync(cts.Token);
            var stderr = await stderrTask;

            logger.LogInformation(
                "[User: {UserId}] External agent '{Agent}' exited with code {ExitCode}",
                user.UserId,
                agent.Name,
                process.ExitCode);

            if (process.ExitCode != 0)
            {
                var errSummary = string.IsNullOrWhiteSpace(stderr) ? "Process exited without stderr output." : stderr;
                WriteEvent(writer, ProcessingStreamEvent.ResultEvent(
                    ProcessingResult.Fail(
                        $"External agent '{agent.Name}' failed (exit {process.ExitCode}): {Truncate(errSummary, 500)}")));
                return;
            }

            if (finalResult is null)
            {
                WriteEvent(writer, ProcessingStreamEvent.ResultEvent(
                    ProcessingResult.Fail(
                        $"External agent '{agent.Name}' completed without emitting a final result event.")));
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            if (process is not null)
                KillSafe(process);

            logger.LogWarning(
                "[User: {UserId}] External agent '{Agent}' timed out after configured timeout",
                user.UserId,
                agent.Name);

            WriteEvent(writer, ProcessingStreamEvent.ResultEvent(
                ProcessingResult.Fail($"External agent '{agent.Name}' timed out.")));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            if (process is not null)
                KillSafe(process);
        }
        catch (Exception ex)
        {
            if (process is not null)
                KillSafe(process);

            logger.LogError(ex,
                "[User: {UserId}] External agent '{Agent}' failed while processing streamed protocol output",
                user.UserId,
                agent.Name);

            WriteEvent(writer, ProcessingStreamEvent.ResultEvent(
                ProcessingResult.Fail($"External agent '{agent.Name}' failed: {ex.Message}")));
        }
        finally
        {
            cts?.Dispose();
            process?.Dispose();
            writer.TryComplete();
        }
    }

    private static void WriteEvent(ChannelWriter<ProcessingStreamEvent> writer, ProcessingStreamEvent streamEvent)
    {
        _ = writer.TryWrite(streamEvent);
    }

    private static async IAsyncEnumerable<ExternalAgentProtocolEvent> ReadProtocolEventsAsync(
        StreamReader stdout,
        string agentName,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        while (true)
        {
            var line = await stdout.ReadLineAsync().WaitAsync(ct);
            if (line is null)
                yield break;

            if (string.IsNullOrWhiteSpace(line))
                continue;

            yield return ParseProtocolLine(line, agentName);
        }
    }

    private static ExternalAgentProtocolEvent ParseProtocolLine(string line, string agentName)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            if (!root.TryGetProperty("type", out var typeEl))
                throw new JsonException("Missing 'type' property.");

            var type = ParseProtocolEventType(typeEl.GetString());
            var message = root.TryGetProperty("message", out var messageEl)
                ? messageEl.GetString()
                : null;
            var text = root.TryGetProperty("text", out var textEl)
                ? textEl.GetString()
                : null;
            var success = root.TryGetProperty("success", out var successEl)
                ? successEl.GetBoolean()
                : (bool?)null;
            var data = root.TryGetProperty("data", out var dataEl)
                ? dataEl.Clone()
                : (JsonElement?)null;

            return new ExternalAgentProtocolEvent(type, message, text, success, data);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"External agent '{agentName}' emitted invalid protocol output: {ex.Message}",
                ex);
        }
    }

    private static ExternalAgentProtocolEventType ParseProtocolEventType(string? rawType) => rawType?.Trim().ToLowerInvariant() switch
    {
        "status" => ExternalAgentProtocolEventType.Status,
        "chunk" => ExternalAgentProtocolEventType.Chunk,
        "result" => ExternalAgentProtocolEventType.Result,
        _ => throw new InvalidOperationException($"Unknown external agent event type '{rawType}'."),
    };

    private static object? ConvertProtocolData(JsonElement? data) => data?.ValueKind switch
    {
        null => null,
        JsonValueKind.Null => null,
        JsonValueKind.Undefined => null,
        JsonValueKind.String => data.Value.GetString(),
        _ => data.Value.Clone(),
    };

    private static void KillSafe(Process process)
    {
        try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : string.Concat(s.AsSpan(0, max), "…");
}

internal sealed record ExternalAgentRequestPayload(
    int ProtocolVersion,
    int AgentId,
    string AgentName,
    string UserId,
    string Input,
    string OutputDestination,
    IReadOnlyDictionary<string, string>? Parameters);

internal enum ExternalAgentProtocolEventType
{
    Status,
    Chunk,
    Result,
}

internal sealed record ExternalAgentProtocolEvent(
    ExternalAgentProtocolEventType Type,
    string? Message = null,
    string? Text = null,
    bool? Success = null,
    JsonElement? Data = null);
