using DataNexus.Models;
using Microsoft.Agents.AI.Workflows;

namespace DataNexus.Agents.Af;

/// <summary>
/// Maps the AF workflow <see cref="Run"/>'s output events to a domain <see cref="ProcessingResult"/>.
///
/// An <see cref="AgentResponseEvent"/> is emitted once per agent as it completes.
/// In a sequential pipeline the final event belongs to the last agent in the chain.
/// <see cref="Enumerable.LastOrDefault{TSource}(IEnumerable{TSource})"/> therefore selects the
/// terminal agent's response — the authoritative result of the entire workflow.
/// If no response event was emitted the workflow produced no output (treated as failure).
/// </summary>
public static class WorkflowResultMapper
{
    public static ProcessingResult Map(Run run, string contextName)
    {
        var terminal = run.NewEvents.OfType<AgentResponseEvent>().LastOrDefault();

        if (terminal is null)
            return ProcessingResult.Fail($"Workflow '{contextName}' produced no output.");

        var text = terminal.Response.Text;
        return ProcessingResult.Ok(text, text);
    }
}
