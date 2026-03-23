using DataNexus.Core;
using DataNexus.Identity;
using DataNexus.Models;
using Microsoft.Agents.AI.Workflows;

namespace DataNexus.Agents.Af;

/// <summary>
/// AF workflow executor for External (CLI/script) agents.
/// Delegates execution to ExternalProcessRunner using the JSON stdin/stdout protocol.
/// Handles both AgentStepInput (entry step) and AgentStepOutput (chained pipeline step).
/// Security is enforced by ExternalProcessRunner (command allowlist, timeout cap, no shell execute).
/// </summary>
internal sealed partial class ExternalAgentExecutor : Executor
{
    private readonly AgentDefinition _agent;
    private readonly UserContext _user;
    private readonly ExternalProcessRunner _runner;
    private readonly ILogger _logger;

    public ExternalAgentExecutor(
        AgentDefinition agent,
        UserContext user,
        ExternalProcessRunner runner,
        ILogger logger) : base($"ExtAgent_{agent.Id}_{agent.Name}")
    {
        _agent = agent;
        _user = user;
        _runner = runner;
        _logger = logger;
    }

    protected override ProtocolBuilder ConfigureProtocol(ProtocolBuilder builder)
    {
        builder.SendsMessage<AgentStepOutput>();
        builder.YieldsOutput<AgentStepOutput>();
        builder.RouteBuilder.AddHandler<AgentStepInput, AgentStepOutput>(
            (input, ctx, ct) => HandleInitialAsync(input, ctx, ct), true);
        builder.RouteBuilder.AddHandler<AgentStepOutput, AgentStepOutput>(
            (prev, ctx, ct) => HandleChainedAsync(prev, ctx, ct), true);
        return builder;
    }

    // Entry handler: first step in single-agent run or head of pipeline.
    [MessageHandler]
    private async ValueTask<AgentStepOutput> HandleInitialAsync(
        AgentStepInput input,
        IWorkflowContext context,
        CancellationToken ct = default) =>
        await RunCoreAsync(input.Data, input.Parameters, context, ct);

    // Chained handler: receives prior step's output in a pipeline.
    [MessageHandler]
    private async ValueTask<AgentStepOutput> HandleChainedAsync(
        AgentStepOutput prev,
        IWorkflowContext context,
        CancellationToken ct = default)
    {
        if (!prev.Success)
            return prev;

        return await RunCoreAsync(prev.Data ?? prev.Message, null, context, ct);
    }

    private async ValueTask<AgentStepOutput> RunCoreAsync(
        string inputData,
        IReadOnlyDictionary<string, string>? parameters,
        IWorkflowContext context,
        CancellationToken ct)
    {
        try
        {
            // Wrap as a ProcessingRequest — ExternalProcessRunner reads agent metadata from AgentDefinition.
            var request = new ProcessingRequest(
                AgentId: _agent.Id,
                InputSource: inputData,
                OutputDestination: string.Empty,
                Parameters: parameters);

            var result = await _runner.RunAsync(_agent, request, _user, ct);

            if (!result.Success)
            {
                _logger.LogWarning(
                    "[User: {UserId}] External agent '{Agent}' failed: {Message}",
                    _user.UserId, _agent.Name, result.Message);
                return new AgentStepOutput(false, $"External agent '{_agent.Name}' failed: {result.Message}");
            }

            var outputData = result.Data?.ToString();
            await context.QueueStateUpdateAsync($"agent_{_agent.Id}_output", outputData ?? result.Message);

            _logger.LogInformation(
                "[User: {UserId}] External agent '{Agent}' completed",
                _user.UserId, _agent.Name);

            return new AgentStepOutput(true, $"External agent '{_agent.Name}' completed", outputData);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[User: {UserId}] External agent '{Agent}' threw",
                _user.UserId, _agent.Name);
            return new AgentStepOutput(false, $"External agent '{_agent.Name}' threw: {ex.Message}");
        }
    }
}
