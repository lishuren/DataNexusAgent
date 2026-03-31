namespace DataNexus.Models;

public sealed record ProcessingRequest(
    int? AgentId,
    string InputSource,
    string OutputDestination,
    string? SkillName = null,
    IReadOnlyDictionary<string, string>? Parameters = null);

public sealed record PipelineRequest(
    string Name,
    IReadOnlyList<int> AgentIds,
    string InputSource,
    string OutputDestination,
    bool EnableSelfCorrection = true,
    int MaxCorrectionAttempts = 3,
    DataNexus.Core.ExecutionMode ExecutionMode = DataNexus.Core.ExecutionMode.Sequential,
    DataNexus.Core.ConcurrentAggregatorMode ConcurrentAggregatorMode = DataNexus.Core.ConcurrentAggregatorMode.Concatenate,
    int GroupChatMaxIterations = 10,
    IReadOnlyDictionary<string, string>? Parameters = null);
