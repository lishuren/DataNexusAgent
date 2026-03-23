using DataNexus.Models;
using Microsoft.Agents.AI.Workflows;

namespace DataNexus.Agents.Af;

/// <summary>
/// Maps the AF workflow Run's output events to a domain ProcessingResult.
///
/// Mapping rules (evaluated against the LAST WorkflowOutputEvent):
///   • RequiresCorrection=true  → Success=false, Warnings=["RequiresCorrection"]
///   • AgentStepOutput.Success=false → ProcessingResult.Fail
///   • AgentStepOutput.Success=true  → ProcessingResult.Ok
///   • No output events         → processing produced no output (treat as failure)
/// </summary>
public static class WorkflowResultMapper
{
    public static ProcessingResult Map(Run run, string contextName)
    {
        // Collect all AgentStepOutput events emitted by the executors.
        var outputs = run.NewEvents
            .OfType<WorkflowOutputEvent>()
            .Where(e => e.Is<AgentStepOutput>())
            .Select(e => e.As<AgentStepOutput>()!)
            .ToList();

        if (outputs.Count == 0)
            return ProcessingResult.Fail($"Workflow '{contextName}' produced no output.");

        // The last output event is the terminal result (deepest step that executed).
        var terminal = outputs[^1];

        if (terminal.RequiresCorrection)
            return new ProcessingResult(
                false,
                terminal.MismatchDetails ?? "Output schema mismatch — correction required.",
                terminal.Data,
                ["RequiresCorrection"]);

        if (!terminal.Success)
            return ProcessingResult.Fail(terminal.Message);

        return ProcessingResult.Ok(terminal.Message, terminal.Data);
    }
}
