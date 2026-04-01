using DataNexus.Core;
using DataNexus.Identity;
using DataNexus.Models;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace DataNexus.Agents;

/// <summary>
/// Creates an <see cref="AIAgent"/> that wraps <see cref="ExternalProcessRunner"/>
/// so external (CLI/script) agents can participate in AF sequential workflows.
/// </summary>
public sealed class ExternalAgentExecutionTrace
{
    public ProcessingResult? LastResult { get; set; }
}

public static class ExternalAgentAdapter
{
    /// <summary>
    /// Builds an AF agent backed by <see cref="ExternalProcessRunner"/>.
    /// The agent reads the last user message as input and writes the process result
    /// as an assistant message.
    /// </summary>
    public static AIAgent Create(
        IChatClient dummyClient,
        ExternalProcessRunner runner,
        AgentDefinition agentDef,
        UserContext user,
        ILogger logger,
        string outputDestination,
        IReadOnlyDictionary<string, string>? parameters,
        ExternalAgentExecutionTrace? trace = null)
    {
        // Use a ChatClientAgent as the inner agent (required by builder), then completely
        // override execution via Use middleware to delegate to ExternalProcessRunner.
        var inner = new ChatClientAgent(dummyClient, name: agentDef.Name, instructions: "");

        return inner.AsBuilder()
            .Use(
                runFunc: async (messages, session, options, _, cancellationToken) =>
                {
                    var request = BuildRequest(agentDef, messages, outputDestination, parameters);

                    logger.LogInformation(
                        "[User: {UserId}] External agent '{Agent}' starting via AF adapter",
                        user.UserId, agentDef.Name);

                    var result = await runner.RunAsync(agentDef, request, user, cancellationToken);
                    trace?.LastResult = result;

                    var outputText = result.Success
                        ? result.Data?.ToString() ?? result.Message
                        : PluginError.Format($"External agent '{agentDef.Name}': {result.Message}");

                    return new AgentResponse([new ChatMessage(ChatRole.Assistant, outputText)]);
                },
                runStreamingFunc: (messages, session, options, _, cancellationToken) =>
                    StreamExternalAsync(runner, agentDef, user, messages, outputDestination, parameters, trace, cancellationToken))
            .Build();
    }

    private static async IAsyncEnumerable<AgentResponseUpdate> StreamExternalAsync(
        ExternalProcessRunner runner,
        AgentDefinition agentDef,
        UserContext user,
        IEnumerable<ChatMessage> messages,
        string outputDestination,
        IReadOnlyDictionary<string, string>? parameters,
        ExternalAgentExecutionTrace? trace,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var request = BuildRequest(agentDef, messages, outputDestination, parameters);
        var emittedChunk = false;

        await foreach (var streamEvent in runner.RunStreamingAsync(agentDef, request, user, cancellationToken)
            .WithCancellation(cancellationToken))
        {
            switch (streamEvent.Type)
            {
                case "chunk" when !string.IsNullOrWhiteSpace(streamEvent.Text):
                    emittedChunk = true;
                    yield return new AgentResponseUpdate(ChatRole.Assistant, streamEvent.Text)
                    {
                        AgentId = agentDef.Name,
                    };
                    break;

                case "result" when streamEvent.Result is { Success: false } failed:
                    trace?.LastResult = failed;
                    yield return new AgentResponseUpdate(
                        ChatRole.Assistant,
                        PluginError.Format($"External agent '{agentDef.Name}': {failed.Message}"))
                    {
                        AgentId = agentDef.Name,
                    };
                    break;

                case "result" when !emittedChunk && streamEvent.Result is { Success: true, Data: not null } completed:
                    trace?.LastResult = completed;
                    yield return new AgentResponseUpdate(ChatRole.Assistant, completed.Data.ToString())
                    {
                        AgentId = agentDef.Name,
                    };
                    break;

                case "result" when !emittedChunk && streamEvent.Result is { Success: true, Message: not null } completed:
                    trace?.LastResult = completed;
                    yield return new AgentResponseUpdate(ChatRole.Assistant, completed.Message)
                    {
                        AgentId = agentDef.Name,
                    };
                    break;

                case "result" when streamEvent.Result is { Success: true } completed:
                    trace?.LastResult = completed;
                    break;
            }
        }
    }

    private static ProcessingRequest BuildRequest(
        AgentDefinition agentDef,
        IEnumerable<ChatMessage> messages,
        string outputDestination,
        IReadOnlyDictionary<string, string>? parameters)
    {
        var inputText = messages.LastOrDefault(message => message.Role == ChatRole.User)?.Text
            ?? messages.LastOrDefault()?.Text
            ?? string.Empty;

        return new ProcessingRequest(agentDef.Id, inputText, outputDestination, Parameters: parameters);
    }
}
