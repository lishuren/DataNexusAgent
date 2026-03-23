using DataNexus.Identity;
using DataNexus.Models;
using Microsoft.Extensions.Options;

namespace DataNexus.Agents;

public sealed class AgentExecutionRuntimeSelector(
    DataNexusEngine legacyRuntime,
    AgentFrameworkExecutionRuntime agentFrameworkRuntime,
    IOptions<AgentRuntimeOptions> options,
    ILogger<AgentExecutionRuntimeSelector> logger) : IAgentExecutionRuntime
{
    public Task<ProcessingResult> ProcessAsync(
        ProcessingRequest request,
        UserContext user,
        CancellationToken ct = default) =>
        GetSelectedRuntime(user).ProcessAsync(request, user, ct);

    public Task<ProcessingResult> RunPipelineAsync(
        PipelineRequest pipeline,
        UserContext user,
        CancellationToken ct = default) =>
        GetSelectedRuntime(user).RunPipelineAsync(pipeline, user, ct);

    private IAgentExecutionRuntime GetSelectedRuntime(UserContext user)
    {
        var runtimeOptions = options.Value;

        if (runtimeOptions.EnableAgentFrameworkPreview &&
            string.Equals(runtimeOptions.Mode, AgentRuntimeMode.AgentFramework, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogInformation(
                "[User: {UserId}] Routing execution through Agent Framework runtime selector.",
                user.UserId);

            return agentFrameworkRuntime;
        }

        logger.LogDebug(
            "[User: {UserId}] Routing execution through legacy runtime selector.",
            user.UserId);

        return legacyRuntime;
    }
}