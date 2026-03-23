using DataNexus.Core;
using DataNexus.Identity;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using System.Text;

namespace DataNexus.Agents.Af;

/// <summary>
/// Builds Microsoft Agent Framework workflows at request time from DB-stored AgentDefinitions.
/// Uses AgentWorkflowBuilder.BuildSequential to compose AIAgent instances into a Workflow.
///
/// Each AgentDefinition is mapped to a ChatClientAgent wrapping either:
///   • AfChatClientProvider.Client  — for LLM agents (skills injected into system prompt)
///   • ExternalProcessChatClient    — for external (CLI/script) agents
///
/// Skill resolution requires a DB query, so BuildAsync is async.
/// </summary>
public sealed class DynamicWorkflowBuilder(
    SkillRegistry skillRegistry,
    ExternalProcessRunner externalRunner,
    AfChatClientProvider chatClientProvider,
    ILoggerFactory loggerFactory)
{
    /// <summary>
    /// Builds an AF Workflow that runs all supplied agents sequentially.
    /// A single-agent list produces a single-step workflow; N agents produce an N-step pipeline.
    /// </summary>
    /// <remarks>
    /// Plugin execution (InputProcessorPlugin / OutputIntegratorPlugin) is NOT performed here.
    /// That is the responsibility of <see cref="AgentFrameworkExecutionRuntime"/>, which calls
    /// this method after pre-processing input and before post-processing output.
    /// </remarks>
    public async Task<Workflow> BuildAsync(
        IReadOnlyList<AgentDefinition> agents,
        UserContext user,
        CancellationToken ct = default)
    {
        if (agents.Count == 0)
            throw new ArgumentException("At least one agent is required.", nameof(agents));

        var aiAgents = new List<AIAgent>(agents.Count);
        foreach (var def in agents)
            aiAgents.Add(await CreateAIAgentAsync(def, user, ct));

        var name = agents.Count == 1
            ? agents[0].Name
            : string.Join(" → ", agents.Select(a => a.Name));

        return AgentWorkflowBuilder.BuildSequential(name, aiAgents);
    }

    private async Task<AIAgent> CreateAIAgentAsync(
        AgentDefinition def,
        UserContext user,
        CancellationToken ct)
    {
        IChatClient client;
        string? instructions = null;

        if (def.ExecutionType == AgentExecutionType.External)
        {
            client = new ExternalProcessChatClient(
                def, user, externalRunner,
                loggerFactory.CreateLogger<ExternalProcessChatClient>());
        }
        else
        {
            var skills = def.SkillNames.Count > 0
                ? await skillRegistry.GetSkillsForUserAsync(user.UserId, ct, [.. def.SkillNames])
                : await skillRegistry.GetSkillsForUserAsync(user.UserId, ct);

            instructions = BuildSystemPrompt(def.SystemPrompt, skills);
            client = chatClientProvider.Client;
        }

        return new ChatClientAgent(client, name: def.Name, instructions: instructions);
    }

    private static string BuildSystemPrompt(string agentPrompt, IReadOnlyList<SkillDefinition> skills)
    {
        if (skills.Count == 0) return agentPrompt;

        var sb = new StringBuilder(agentPrompt);
        sb.AppendLine().AppendLine("## Loaded Skills");
        foreach (var skill in skills)
        {
            sb.AppendLine($"### {skill.Name} ({skill.Scope})");
            sb.AppendLine(skill.Instructions);
            sb.AppendLine();
        }
        return sb.ToString();
    }
}
