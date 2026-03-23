using System.Diagnostics;
using System.Text;
using System.Text.Json;
using DataNexus.Core;
using DataNexus.Identity;
using DataNexus.Models;
using Microsoft.Extensions.Options;

namespace DataNexus.Agents;

/// <summary>
/// Executes external (CLI / script) agents as child processes.
///
/// Protocol:
///   • stdin  — receives a JSON object with { input, parameters, userId }.
///   • stdout — must return a JSON object with { success, message, data? }.
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
    private readonly ExternalAgentOptions _opts = options.Value;

    public async Task<ProcessingResult> RunAsync(
        AgentDefinition agent,
        ProcessingRequest request,
        UserContext user,
        CancellationToken ct)
    {
        // ── Guard: feature enabled ──
        if (!_opts.Enabled)
            return ProcessingResult.Fail("External agent execution is disabled by configuration.");

        // ── Guard: command allowlist ──
        var command = agent.Command
            ?? throw new InvalidOperationException($"External agent '{agent.Name}' has no Command configured.");

        if (!IsCommandAllowed(command))
        {
            logger.LogWarning(
                "[User: {UserId}] Blocked external agent '{Agent}': command '{Command}' not in allowlist",
                user.UserId, agent.Name, command);
            return ProcessingResult.Fail($"Command '{command}' is not permitted.");
        }

        // ── Guard: working directory ──
        string? workDir = ResolveWorkingDirectory(agent.WorkingDirectory);

        // ── Build process ──
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

        // Split arguments safely (no shell interpretation)
        foreach (var arg in SplitArguments(agent.Arguments))
            psi.ArgumentList.Add(arg);

        // ── Build stdin payload ──
        var payload = JsonSerializer.Serialize(new
        {
            input = request.InputSource,
            parameters = request.Parameters,
            userId = user.UserId,
            outputDestination = request.OutputDestination,
        });

        logger.LogInformation(
            "[User: {UserId}] Starting external agent '{Agent}': {Command} (timeout {Timeout}s)",
            user.UserId, agent.Name, command, timeout);

        // ── Execute ──
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(timeout));

        Process process;
        try
        {
            process = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start external process.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "[User: {UserId}] External agent '{Agent}' failed to start",
                user.UserId, agent.Name);
            return ProcessingResult.Fail($"Failed to start process: {ex.Message}");
        }

        try
        {
            // Write input to stdin and close — ensure stdin closes even if write fails
            try
            {
                await process.StandardInput.WriteAsync(payload);
            }
            finally
            {
                process.StandardInput.Close();
            }

            // Read stdout + stderr concurrently
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(cts.Token);

            await process.WaitForExitAsync(cts.Token);

            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            logger.LogInformation(
                "[User: {UserId}] External agent '{Agent}' exited with code {ExitCode}",
                user.UserId, agent.Name, process.ExitCode);

            if (process.ExitCode != 0)
            {
                var errSummary = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
                return ProcessingResult.Fail(
                    $"External agent '{agent.Name}' failed (exit {process.ExitCode}): {Truncate(errSummary, 500)}");
            }

            // ── Parse stdout JSON ──
            return ParseStdoutResult(stdout, agent.Name);
        }
        catch (OperationCanceledException)
        {
            KillSafe(process);
            logger.LogWarning(
                "[User: {UserId}] External agent '{Agent}' timed out after {Timeout}s",
                user.UserId, agent.Name, timeout);
            return ProcessingResult.Fail(
                $"External agent '{agent.Name}' timed out after {timeout}s.");
        }
        finally
        {
            process.Dispose();
        }
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

    private static ProcessingResult ParseStdoutResult(string stdout, string agentName)
    {
        if (string.IsNullOrWhiteSpace(stdout))
            return ProcessingResult.Ok($"Agent '{agentName}' completed (no output).", null);

        try
        {
            using var doc = JsonDocument.Parse(stdout);
            var root = doc.RootElement;

            var success = root.TryGetProperty("success", out var s) && s.GetBoolean();
            var message = root.TryGetProperty("message", out var m) ? m.GetString() : null;
            var data = root.TryGetProperty("data", out var d) ? d.GetRawText() : null;

            return success
                ? ProcessingResult.Ok(message ?? $"Agent '{agentName}' completed.", data)
                : ProcessingResult.Fail(message ?? $"Agent '{agentName}' returned failure.");
        }
        catch (JsonException)
        {
            // Not JSON — treat raw stdout as the result data
            return ProcessingResult.Ok($"Agent '{agentName}' completed.", stdout);
        }
    }

    private static void KillSafe(Process process)
    {
        try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : string.Concat(s.AsSpan(0, max), "…");
}
