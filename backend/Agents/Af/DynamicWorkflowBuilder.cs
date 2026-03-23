using DataNexus.Core;
using DataNexus.Identity;
using DataNexus.Plugins;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;

namespace DataNexus.Agents.Af;

/// <summary>
/// Builds Microsoft Agent Framework workflows at request time from DB-stored AgentDefinitions.
/// Scoped per request so each run gets independent executor instances with their own state.
///
/// Pipeline shape (N agents):
///   Entry executor handles AgentStepInput.
///   Each subsequent executor handles AgentStepOutput from the prior step.
///   Conditional edges only forward successful outputs; failures stop propagation.
///   All executors auto-yield their output (ReflectingExecutor default behavior).
/// </summary>
public sealed class DynamicWorkflowBuilder(
    SkillRegistry skillRegistry,
    InputProcessorPlugin inputPlugin,
    OutputIntegratorPlugin outputPlugin,
    ExternalProcessRunner externalRunner,
    AfChatClientProvider chatClientProvider,
    ILoggerFactory loggerFactory)
{
    /// <summary>
    /// Builds a single-agent workflow that accepts an AgentStepInput and yields one AgentStepOutput.
    /// </summary>
    public Workflow BuildSingleAgent(AgentDefinition agent, UserContext user)
    {
        var executor = CreateExecutor(agent, user);
        return new WorkflowBuilder(executor)
            .Build();
    }

    /// <summary>
    /// Builds a pipeline workflow that chains N agents sequentially.
    /// Each agent receives the previous agent's output (on success) as its input.
    /// </summary>
    public Workflow BuildPipeline(
        IReadOnlyList<AgentDefinition> agents, UserContext user)
    {
        if (agents.Count == 0)
            throw new ArgumentException("Pipeline must contain at least one agent.", nameof(agents));

        if (agents.Count == 1)
            return BuildSingleAgent(agents[0], user);

        var executors = agents.Select(a => CreateExecutor(a, user)).ToArray();

        var builder = new WorkflowBuilder(executors[0]);

        // Chain each executor to the next; only forward on success.
        for (int i = 0; i < executors.Length - 1; i++)
            builder.AddEdge<AgentStepOutput>(
                executors[i].BindExecutor(),
                executors[i + 1].BindExecutor(),
                condition: o => o is not null && o.Success);

        return builder.Build();
    }

    private Executor CreateExecutor(AgentDefinition agent, UserContext user) =>
        agent.ExecutionType == AgentExecutionType.External
            ? new ExternalAgentExecutor(agent, user, externalRunner,
                loggerFactory.CreateLogger<ExternalAgentExecutor>())
            : new LlmAgentExecutor(agent, user, skillRegistry, inputPlugin, outputPlugin,
                chatClientProvider.Client, loggerFactory.CreateLogger<LlmAgentExecutor>());
}
