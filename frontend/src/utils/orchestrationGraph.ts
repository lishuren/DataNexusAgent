import type {
  Agent,
  OrchestrationGraph,
  OrchestrationGraphEdge,
  OrchestrationGraphNode,
  OrchestrationStep,
} from "@/types/api";

const makeId = (prefix: string) => {
  if (typeof crypto !== "undefined" && typeof crypto.randomUUID === "function") {
    return `${prefix}-${crypto.randomUUID()}`;
  }

  return `${prefix}-${Date.now()}-${Math.random().toString(36).slice(2, 10)}`;
};

export function cloneGraph(graph: OrchestrationGraph): OrchestrationGraph {
  return {
    nodes: graph.nodes.map((node) => ({
      ...node,
      parameters: node.parameters ? { ...node.parameters } : null,
    })),
    edges: graph.edges.map((edge) => ({ ...edge })),
  };
}

export function graphToSteps(graph: OrchestrationGraph): OrchestrationStep[] {
  return [...graph.nodes]
    .sort((left, right) => left.displayOrder - right.displayOrder)
    .map((node, index) => ({
      stepNumber: index + 1,
      title: node.title,
      description: node.description,
      agentId: node.agentId,
      agentName: node.agentName,
      isEdited: node.isEdited,
      promptOverride: node.promptOverride,
      parameters: node.parameters,
    }));
}

export function buildGraphFromSteps(steps: OrchestrationStep[]): OrchestrationGraph {
  const nodes = steps.map<OrchestrationGraphNode>((step, index) => ({
    id: makeId("node"),
    displayOrder: index + 1,
    title: step.title,
    description: step.description,
    agentId: step.agentId,
    agentName: step.agentName,
    isEdited: step.isEdited,
    promptOverride: step.promptOverride,
    parameters: step.parameters,
    positionX: 120 + index * 220,
    positionY: 120,
  }));

  const edges = nodes.slice(0, -1).map<OrchestrationGraphEdge>((node, index) => ({
    id: makeId("edge"),
    sourceNodeId: node.id,
    targetNodeId: nodes[index + 1]!.id,
  }));

  return { nodes, edges };
}

export function createGraphNode(agent?: Agent, order = 1): OrchestrationGraphNode {
  return {
    id: makeId("node"),
    displayOrder: order,
    title: agent?.name ?? "New Node",
    description: "",
    agentId: agent?.id ?? 0,
    agentName: agent?.name ?? "",
    isEdited: true,
    promptOverride: null,
    parameters: null,
    positionX: 120 + (order - 1) * 180,
    positionY: 120,
  };
}

export function summarizeGraph(graph: OrchestrationGraph): Array<{ id: string; label: string }> {
  const nodeLookup = new Map(graph.nodes.map((node) => [node.id, node]));
  return graph.edges.map((edge) => ({
    id: edge.id,
    label: `${nodeLookup.get(edge.sourceNodeId)?.title ?? edge.sourceNodeId} -> ${nodeLookup.get(edge.targetNodeId)?.title ?? edge.targetNodeId}`,
  }));
}

export function validateGraph(graph: OrchestrationGraph): string[] {
  if (graph.nodes.length < 1) {
    return ["Add at least one node."];
  }

  const issues: string[] = [];
  const nodeIds = new Set<string>();
  const indegree = new Map<string, number>();
  const outgoing = new Map<string, string[]>();
  const edgePairs = new Set<string>();

  for (const node of graph.nodes) {
    if (!node.id.trim()) {
      issues.push("Every node needs an id.");
      continue;
    }
    if (nodeIds.has(node.id)) {
      issues.push(`Duplicate node id: ${node.id}`);
    }
    nodeIds.add(node.id);
    indegree.set(node.id, 0);
    outgoing.set(node.id, []);
  }

  for (const edge of graph.edges) {
    if (!nodeIds.has(edge.sourceNodeId) || !nodeIds.has(edge.targetNodeId)) {
      issues.push(`Edge ${edge.id} references a missing node.`);
      continue;
    }
    if (edge.sourceNodeId === edge.targetNodeId) {
      issues.push(`Edge ${edge.id} cannot connect a node to itself.`);
      continue;
    }
    const pair = `${edge.sourceNodeId}->${edge.targetNodeId}`;
    if (edgePairs.has(pair)) {
      issues.push(`Duplicate edge ${pair}.`);
      continue;
    }
    edgePairs.add(pair);
    indegree.set(edge.targetNodeId, (indegree.get(edge.targetNodeId) ?? 0) + 1);
    outgoing.get(edge.sourceNodeId)!.push(edge.targetNodeId);
  }

  const roots = graph.nodes.filter((node) => (indegree.get(node.id) ?? 0) === 0);
  if (roots.length !== 1) {
    issues.push("Graph workflows currently require exactly one start node.");
  }

  const terminals = graph.nodes.filter((node) => (outgoing.get(node.id) ?? []).length === 0);
  if (terminals.length !== 1) {
    issues.push("Graph workflows currently require exactly one terminal node.");
  }

  const queue = [...graph.nodes.filter((node) => (indegree.get(node.id) ?? 0) === 0).map((node) => node.id)];
  const indegreeCopy = new Map(indegree);
  let visited = 0;

  while (queue.length > 0) {
    const nodeId = queue.shift()!;
    visited += 1;

    for (const targetId of outgoing.get(nodeId) ?? []) {
      const nextCount = (indegreeCopy.get(targetId) ?? 0) - 1;
      indegreeCopy.set(targetId, nextCount);
      if (nextCount === 0) {
        queue.push(targetId);
      }
    }
  }

  if (visited !== graph.nodes.length) {
    issues.push("Graph workflows must be acyclic in v1.");
  }

  if (roots.length === 1) {
    const reachable = new Set<string>([roots[0]!.id]);
    const traversal = [roots[0]!.id];

    while (traversal.length > 0) {
      const nodeId = traversal.shift()!;
      for (const targetId of outgoing.get(nodeId) ?? []) {
        if (!reachable.has(targetId)) {
          reachable.add(targetId);
          traversal.push(targetId);
        }
      }
    }

    if (reachable.size !== graph.nodes.length) {
      issues.push("Every node must be reachable from the start node.");
    }
  }

  return [...new Set(issues)];
}