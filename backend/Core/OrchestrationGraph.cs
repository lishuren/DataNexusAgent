namespace DataNexus.Core;

public enum OrchestrationWorkflowKind
{
    Structured,
    Graph,
}

public sealed record OrchestrationGraph(
    IReadOnlyList<OrchestrationGraphNode> Nodes,
    IReadOnlyList<OrchestrationGraphEdge> Edges);

public sealed record OrchestrationGraphNode
{
    public string Id { get; init; } = string.Empty;
    public int DisplayOrder { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public int AgentId { get; init; }
    public string AgentName { get; init; } = string.Empty;
    public bool IsEdited { get; init; }
    public string? PromptOverride { get; init; }
    public Dictionary<string, string>? Parameters { get; init; }
    public double PositionX { get; init; }
    public double PositionY { get; init; }
}

public sealed record OrchestrationGraphEdge
{
    public string Id { get; init; } = string.Empty;
    public string SourceNodeId { get; init; } = string.Empty;
    public string TargetNodeId { get; init; } = string.Empty;
}

public static class OrchestrationGraphRules
{
    public static IReadOnlyList<OrchestrationStep> NormalizeSteps(IReadOnlyList<OrchestrationStep> steps)
    {
        if (steps.Count < 1)
            throw new InvalidOperationException("At least one step is required.");

        return steps
            .Select((step, index) => new OrchestrationStep
            {
                StepNumber = index + 1,
                Title = step.Title?.Trim() ?? string.Empty,
                Description = step.Description?.Trim() ?? string.Empty,
                AgentId = step.AgentId,
                AgentName = step.AgentName?.Trim() ?? string.Empty,
                IsEdited = step.IsEdited,
                PromptOverride = string.IsNullOrWhiteSpace(step.PromptOverride)
                    ? null
                    : step.PromptOverride.Trim(),
                Parameters = step.Parameters is { Count: > 0 }
                    ? new Dictionary<string, string>(step.Parameters, StringComparer.Ordinal)
                    : null,
            })
            .ToList();
    }

    public static OrchestrationGraph NormalizeGraph(OrchestrationGraph? graph)
    {
        if (graph is null)
            throw new InvalidOperationException("Graph data is required for graph orchestrations.");

        if (graph.Nodes.Count < 1)
            throw new InvalidOperationException("Graph orchestrations require at least one node.");

        var normalizedNodes = new List<OrchestrationGraphNode>(graph.Nodes.Count);
        var nodeIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (node, index) in graph.Nodes.Select((item, idx) => (item, idx)))
        {
            var nodeId = node.Id?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(nodeId))
                throw new InvalidOperationException("Every graph node must have an id.");

            if (!nodeIds.Add(nodeId))
                throw new InvalidOperationException($"Duplicate graph node id '{nodeId}'.");

            normalizedNodes.Add(node with
            {
                Id = nodeId,
                DisplayOrder = node.DisplayOrder > 0 ? node.DisplayOrder : index + 1,
                Title = node.Title?.Trim() ?? string.Empty,
                Description = node.Description?.Trim() ?? string.Empty,
                AgentName = node.AgentName?.Trim() ?? string.Empty,
                PromptOverride = string.IsNullOrWhiteSpace(node.PromptOverride)
                    ? null
                    : node.PromptOverride.Trim(),
                Parameters = node.Parameters is { Count: > 0 }
                    ? new Dictionary<string, string>(node.Parameters, StringComparer.Ordinal)
                    : null,
            });
        }

        var normalizedEdges = new List<OrchestrationGraphEdge>(graph.Edges.Count);
        var edgeIds = new HashSet<string>(StringComparer.Ordinal);
        var edgePairs = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (edge, index) in graph.Edges.Select((item, idx) => (item, idx)))
        {
            var edgeId = string.IsNullOrWhiteSpace(edge.Id) ? $"edge-{index + 1}" : edge.Id.Trim();
            if (!edgeIds.Add(edgeId))
                throw new InvalidOperationException($"Duplicate graph edge id '{edgeId}'.");

            var sourceNodeId = edge.SourceNodeId?.Trim() ?? string.Empty;
            var targetNodeId = edge.TargetNodeId?.Trim() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(sourceNodeId) || string.IsNullOrWhiteSpace(targetNodeId))
                throw new InvalidOperationException($"Graph edge '{edgeId}' must have both source and target ids.");

            if (!nodeIds.Contains(sourceNodeId))
                throw new InvalidOperationException($"Graph edge '{edgeId}' references unknown source node '{sourceNodeId}'.");

            if (!nodeIds.Contains(targetNodeId))
                throw new InvalidOperationException($"Graph edge '{edgeId}' references unknown target node '{targetNodeId}'.");

            if (string.Equals(sourceNodeId, targetNodeId, StringComparison.Ordinal))
                throw new InvalidOperationException($"Graph edge '{edgeId}' cannot point a node to itself.");

            var edgePair = $"{sourceNodeId}->{targetNodeId}";
            if (!edgePairs.Add(edgePair))
                throw new InvalidOperationException($"Duplicate graph edge '{edgePair}'.");

            normalizedEdges.Add(edge with
            {
                Id = edgeId,
                SourceNodeId = sourceNodeId,
                TargetNodeId = targetNodeId,
            });
        }

        ValidateTopology(normalizedNodes, normalizedEdges);

        return new OrchestrationGraph(
            normalizedNodes.OrderBy(node => node.DisplayOrder).ToList(),
            normalizedEdges);
    }

    public static IReadOnlyList<OrchestrationStep> ToStructuredSteps(this OrchestrationGraph graph)
    {
        return graph.Nodes
            .OrderBy(node => node.DisplayOrder)
            .Select((node, index) => new OrchestrationStep
            {
                StepNumber = index + 1,
                Title = node.Title,
                Description = node.Description,
                AgentId = node.AgentId,
                AgentName = node.AgentName,
                IsEdited = node.IsEdited,
                PromptOverride = node.PromptOverride,
                Parameters = node.Parameters,
            })
            .ToList();
    }

    public static IReadOnlyList<string> GetRootNodeIds(OrchestrationGraph graph)
    {
        var incomingCounts = graph.Nodes.ToDictionary(node => node.Id, _ => 0, StringComparer.Ordinal);
        foreach (var edge in graph.Edges)
            incomingCounts[edge.TargetNodeId]++;

        return incomingCounts
            .Where(entry => entry.Value == 0)
            .Select(entry => entry.Key)
            .ToList();
    }

    public static IReadOnlyList<string> GetTerminalNodeIds(OrchestrationGraph graph)
    {
        var outgoingCounts = graph.Nodes.ToDictionary(node => node.Id, _ => 0, StringComparer.Ordinal);
        foreach (var edge in graph.Edges)
            outgoingCounts[edge.SourceNodeId]++;

        return outgoingCounts
            .Where(entry => entry.Value == 0)
            .Select(entry => entry.Key)
            .ToList();
    }

    private static void ValidateTopology(
        IReadOnlyList<OrchestrationGraphNode> nodes,
        IReadOnlyList<OrchestrationGraphEdge> edges)
    {
        var roots = GetRootNodeIds(new OrchestrationGraph(nodes, edges));
        if (roots.Count != 1)
            throw new InvalidOperationException("Graph orchestrations currently require exactly one start node.");

        var terminals = GetTerminalNodeIds(new OrchestrationGraph(nodes, edges));
        if (terminals.Count != 1)
            throw new InvalidOperationException("Graph orchestrations currently require exactly one terminal node.");

        var adjacency = nodes.ToDictionary(
            node => node.Id,
            _ => new List<string>(),
            StringComparer.Ordinal);
        var indegree = nodes.ToDictionary(
            node => node.Id,
            _ => 0,
            StringComparer.Ordinal);

        foreach (var edge in edges)
        {
            adjacency[edge.SourceNodeId].Add(edge.TargetNodeId);
            indegree[edge.TargetNodeId]++;
        }

        var reachable = new HashSet<string>(StringComparer.Ordinal);
        var traversalQueue = new Queue<string>();
        traversalQueue.Enqueue(roots[0]);
        reachable.Add(roots[0]);

        while (traversalQueue.Count > 0)
        {
            var nodeId = traversalQueue.Dequeue();
            foreach (var targetNodeId in adjacency[nodeId])
            {
                if (reachable.Add(targetNodeId))
                    traversalQueue.Enqueue(targetNodeId);
            }
        }

        if (reachable.Count != nodes.Count)
            throw new InvalidOperationException("Every graph node must be reachable from the start node.");

        var queue = new Queue<string>(indegree.Where(entry => entry.Value == 0).Select(entry => entry.Key));
        var visitedCount = 0;

        while (queue.Count > 0)
        {
            var nodeId = queue.Dequeue();
            visitedCount++;

            foreach (var targetNodeId in adjacency[nodeId])
            {
                indegree[targetNodeId]--;
                if (indegree[targetNodeId] == 0)
                    queue.Enqueue(targetNodeId);
            }
        }

        if (visitedCount != nodes.Count)
            throw new InvalidOperationException("Graph orchestrations must be acyclic in v1.");
    }
}