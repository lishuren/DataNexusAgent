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
        ILogger logger)
    {
        // Use a ChatClientAgent as the inner agent (required by builder), then completely
        // override execution via Use middleware to delegate to ExternalProcessRunner.
        var inner = new ChatClientAgent(dummyClient, name: agentDef.Name, instructions: "");

        return inner.AsBuilder()
            .Use(
                runFunc: async (messages, session, options, _, cancellationToken) =>
                {
                    var inputText = messages.LastOrDefault(m => m.Role == ChatRole.User)?.Text
                        ?? messages.LastOrDefault()?.Text
                        ?? string.Empty;

                    logger.LogInformation(
                        "[User: {UserId}] External agent '{Agent}' starting via AF adapter",
                        user.UserId, agentDef.Name);

                    var request = new ProcessingRequest(agentDef.Id, inputText, "stdout");
                    var result = await runner.RunAsync(agentDef, request, user, cancellationToken);

                    var outputText = result.Success
                        ? result.Data?.ToString() ?? result.Message
                        : $"[ERROR] {result.Message}";

                    return new AgentResponse([new ChatMessage(ChatRole.Assistant, outputText)]);
                },
                runStreamingFunc: null)
            .Build();
    }
}
