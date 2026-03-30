using DataNexus.Core;
using DataNexus.Identity;
using DataNexus.Models;

namespace DataNexus.Agents;

public interface IAgentExecutionRuntime
{
    Task<ProcessingResult> ProcessAsync(
        ProcessingRequest request,
        UserContext user,
        CancellationToken ct = default);

    Task<ProcessingResult> RunPipelineAsync(
        PipelineRequest pipeline,
        UserContext user,
        CancellationToken ct = default);

    Task<ProcessingResult> RunOrchestrationAsync(
        OrchestrationDefinition orchestration,
        string inputSource,
        UserContext user,
        CancellationToken ct = default);
}