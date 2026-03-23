using DataNexus.Core;
using DataNexus.Identity;
using DataNexus.Models;
using Microsoft.Extensions.AI;
using System.Runtime.CompilerServices;

namespace DataNexus.Agents.Af;

/// <summary>
/// IChatClient adapter for external (CLI/script) agents.
/// Wraps ExternalProcessRunner so that external agents can participate in
/// AgentWorkflowBuilder.BuildSequential pipelines alongside LLM agents using a uniform interface.
///
/// The last ChatRole.User message in the conversation history is used as the input data
/// sent to the external process via the JSON stdin/stdout protocol.
/// </summary>
internal sealed class ExternalProcessChatClient(
    AgentDefinition agent,
    UserContext user,
    ExternalProcessRunner runner,
    ILogger<ExternalProcessChatClient> logger) : IChatClient
{
    public ChatClientMetadata Metadata => new(defaultModelId: agent.Name);

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken ct = default)
    {
        var input = messages.LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? string.Empty;

        var request = new ProcessingRequest(
            AgentId: agent.Id,
            InputSource: input,
            OutputDestination: string.Empty,
            Parameters: null);

        var result = await runner.RunAsync(agent, request, user, ct);

        if (!result.Success)
        {
            logger.LogWarning(
                "[User: {UserId}] External agent '{Agent}' failed: {Message}",
                user.UserId, agent.Name, result.Message);
        }
        else
        {
            logger.LogInformation(
                "[User: {UserId}] External agent '{Agent}' completed",
                user.UserId, agent.Name);
        }

        var text = result.Success
            ? (result.Data?.ToString() ?? result.Message)
            : $"External agent '{agent.Name}' failed: {result.Message}";

        return new ChatResponse(new ChatMessage(ChatRole.Assistant, text));
    }

    /// <remarks>
    /// External processes execute synchronously to completion and return a single JSON response;
    /// there is no native stream interface. This method delegates to <see cref="GetResponseAsync"/>
    /// and yields the complete response as a single update.
    /// </remarks>
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var response = await GetResponseAsync(messages, options, ct);
        yield return new ChatResponseUpdate(ChatRole.Assistant, response.Text);
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}
