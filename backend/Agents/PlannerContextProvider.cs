using System.Text;
using DataNexus.Core;
using Microsoft.Agents.AI;

namespace DataNexus.Agents;

internal sealed class PlannerContextProvider(
    IReadOnlyList<AgentDefinition> candidates,
    ExecutionMode requestedExecutionMode,
    OrchestrationWorkflowKind requestedWorkflowKind) : AIContextProvider
{
    protected override ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return ValueTask.FromResult(new AIContext
        {
            Instructions = BuildPlannerContext(candidates, requestedExecutionMode, requestedWorkflowKind),
        });
    }

    private static string BuildPlannerContext(
        IReadOnlyList<AgentDefinition> candidates,
        ExecutionMode requestedExecutionMode,
        OrchestrationWorkflowKind requestedWorkflowKind)
    {
        var builder = new StringBuilder();

        builder.AppendLine("You are planning a DataNexus orchestration against the exact agent catalog below.");
        builder.AppendLine($"Target workflow kind: {requestedWorkflowKind}.");
        builder.AppendLine($"Target execution mode: {requestedExecutionMode}.");
        builder.AppendLine();
        builder.AppendLine("Planning rules:");
        builder.AppendLine("1. Every step must reference an agentId from the available catalog.");
        builder.AppendLine("2. Use the minimum number of steps that can complete the user's goal safely.");
        builder.AppendLine("3. Choose agents based on their description, execution type, plugins, and configured skills.");
        builder.AppendLine("4. If a step needs file parsing or ingestion, prefer agents with InputProcessor.");
        builder.AppendLine("5. If a step needs API or database output, prefer agents with OutputIntegrator.");
        builder.AppendLine("6. Do not invent capabilities that are not present in the catalog.");
        builder.AppendLine("7. Return data that matches the requested structured schema exactly.");
        builder.AppendLine("8. Keep the workflow easy to review and approve.");

        if (requestedWorkflowKind == OrchestrationWorkflowKind.Graph)
        {
            builder.AppendLine("9. Graph drafts must be acyclic and use exactly one start node and one terminal node.");
            builder.AppendLine("10. Use edges to express branches and joins only when they clearly improve the workflow.");
        }
        else
        {
            builder.AppendLine("9. Structured drafts must be a simple ordered step list.");
        }

        builder.AppendLine();

        if (requestedWorkflowKind == OrchestrationWorkflowKind.Graph)
            builder.AppendLine($"Workflow kind guidance: {DescribeWorkflowKind(requestedWorkflowKind)}");
        else
            builder.AppendLine($"Execution mode guidance: {DescribeExecutionMode(requestedExecutionMode)}");

        builder.AppendLine();
        builder.AppendLine("Available agents:");

        foreach (var candidate in candidates.OrderBy(agent => agent.Name, StringComparer.OrdinalIgnoreCase))
        {
            var plugins = candidate.PluginNames.Count > 0
                ? string.Join(',', candidate.PluginNames)
                : "none";
            var skills = candidate.SkillNames.Count > 0
                ? string.Join(',', candidate.SkillNames)
                : "none";
            var description = string.IsNullOrWhiteSpace(candidate.Description)
                ? "No description provided"
                : candidate.Description.Replace('\n', ' ').Replace('\r', ' ');

            builder.Append("- AgentId=")
                .Append(candidate.Id)
                .Append(", Name=\"")
                .Append(candidate.Name)
                .Append("\", Type=")
                .Append(candidate.ExecutionType)
                .Append(", Plugins=\"")
                .Append(plugins)
                .Append("\", Skills=\"")
                .Append(skills)
                .Append("\", Description=\"")
                .Append(description)
                .AppendLine("\"");
        }

        return builder.ToString();
    }

    private static string DescribeExecutionMode(ExecutionMode requestedExecutionMode) => requestedExecutionMode switch
    {
        ExecutionMode.Concurrent =>
            "Favor independent branches that can run in parallel and whose outputs can be merged later.",
        ExecutionMode.Handoff =>
            "Choose a strong coordinator or triage step first, then add specialist agents that can receive handoffs.",
        ExecutionMode.GroupChat =>
            "Choose complementary agents that benefit from iterative discussion instead of a strict linear chain.",
        _ =>
            "Favor a clean linear chain where each step prepares the next step's input.",
    };

    private static string DescribeWorkflowKind(OrchestrationWorkflowKind requestedWorkflowKind) => requestedWorkflowKind switch
    {
        OrchestrationWorkflowKind.Graph =>
            "Return a DAG that can branch and merge, with one root node and one terminal node. Prefer the smallest graph that still captures meaningful parallel work.",
        _ =>
            "Return a simple ordered list of steps.",
    };
}