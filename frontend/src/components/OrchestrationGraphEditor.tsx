import type { Agent, OrchestrationGraph, OrchestrationGraphNode } from "@/types/api";
import { createGraphNode, summarizeGraph, validateGraph } from "@/utils/orchestrationGraph";

interface OrchestrationGraphEditorProps {
  graph: OrchestrationGraph;
  agents: Agent[];
  readOnly?: boolean;
  onChange?: (graph: OrchestrationGraph) => void;
}

export function OrchestrationGraphEditor({
  graph,
  agents,
  readOnly = false,
  onChange,
}: OrchestrationGraphEditorProps) {
  const issues = validateGraph(graph);
  const edges = summarizeGraph(graph);
  const orderedNodes = [...graph.nodes].sort((left, right) => left.displayOrder - right.displayOrder);

  const updateGraph = (nextGraph: OrchestrationGraph) => {
    onChange?.(nextGraph);
  };

  const updateNode = (nodeId: string, patch: Partial<OrchestrationGraphNode>) => {
    if (readOnly) return;

    updateGraph({
      ...graph,
      nodes: graph.nodes.map((node) => (
        node.id === nodeId
          ? { ...node, ...patch, isEdited: true }
          : node
      )),
    });
  };

  const addNode = () => {
    if (readOnly || agents.length === 0) return;

    updateGraph({
      ...graph,
      nodes: [...graph.nodes, createGraphNode(agents[0], graph.nodes.length + 1)],
    });
  };

  const removeNode = (nodeId: string) => {
    if (readOnly) return;

    const nodes = graph.nodes
      .filter((node) => node.id !== nodeId)
      .sort((left, right) => left.displayOrder - right.displayOrder)
      .map((node, index) => ({ ...node, displayOrder: index + 1 }));

    updateGraph({
      nodes,
      edges: graph.edges.filter((edge) => edge.sourceNodeId !== nodeId && edge.targetNodeId !== nodeId),
    });
  };

  const toggleEdge = (sourceNodeId: string, targetNodeId: string) => {
    if (readOnly || sourceNodeId === targetNodeId) return;

    const existing = graph.edges.find((edge) => edge.sourceNodeId === sourceNodeId && edge.targetNodeId === targetNodeId);
    if (existing) {
      updateGraph({
        ...graph,
        edges: graph.edges.filter((edge) => edge.id !== existing.id),
      });
      return;
    }

    const edgeId = `edge-${sourceNodeId}-${targetNodeId}`;
    updateGraph({
      ...graph,
      edges: [
        ...graph.edges,
        { id: edgeId, sourceNodeId, targetNodeId },
      ],
    });
  };

  const isChecked = (sourceNodeId: string, targetNodeId: string) =>
    graph.edges.some((edge) => edge.sourceNodeId === sourceNodeId && edge.targetNodeId === targetNodeId);

  return (
    <div style={{ display: "flex", flexDirection: "column", gap: "0.75rem" }}>
      <div style={{
        display: "flex",
        justifyContent: "space-between",
        gap: "0.75rem",
        alignItems: "center",
        flexWrap: "wrap",
      }}>
        <div style={{ fontSize: "0.8rem", color: "var(--text-muted)" }}>
          Branch by selecting multiple next nodes. Join branches by targeting the same downstream node.
        </div>
        {!readOnly && (
          <button className="btn btn-outline btn-sm" onClick={addNode} disabled={agents.length === 0}>
            + Add Node
          </button>
        )}
      </div>

      {issues.length > 0 && (
        <div style={{
          padding: "0.6rem 0.75rem",
          borderRadius: "0.5rem",
          border: "1px solid rgba(224,85,85,0.35)",
          background: "rgba(224,85,85,0.08)",
          fontSize: "0.8rem",
          color: "var(--danger)",
        }}>
          {issues.map((issue) => (
            <div key={issue}>{issue}</div>
          ))}
        </div>
      )}

      <div style={{
        padding: "0.65rem 0.75rem",
        border: "1px solid var(--border)",
        borderRadius: "0.5rem",
        background: "rgba(245,166,35,0.05)",
      }}>
        <div style={{ fontWeight: 600, fontSize: "0.8rem", marginBottom: "0.35rem" }}>Flow Map</div>
        {edges.length === 0 ? (
          <div style={{ fontSize: "0.8rem", color: "var(--text-muted)" }}>
            No edges yet. A single node workflow is valid; add edges to branch or chain nodes.
          </div>
        ) : (
          <div style={{ display: "flex", flexWrap: "wrap", gap: "0.35rem" }}>
            {edges.map((edge) => (
              <span key={edge.id} className="badge badge-private" style={{ textTransform: "none" }}>
                {edge.label}
              </span>
            ))}
          </div>
        )}
      </div>

      <div style={{ display: "grid", gridTemplateColumns: "repeat(auto-fit, minmax(280px, 1fr))", gap: "0.75rem" }}>
        {orderedNodes.map((node) => {
          const outgoingTargets = graph.edges
            .filter((edge) => edge.sourceNodeId === node.id)
            .map((edge) => orderedNodes.find((candidate) => candidate.id === edge.targetNodeId)?.title ?? edge.targetNodeId);

          return (
            <div
              key={node.id}
              style={{
                border: "1px solid var(--border)",
                borderRadius: "0.65rem",
                background: "var(--surface)",
                padding: "0.75rem",
                display: "flex",
                flexDirection: "column",
                gap: "0.55rem",
              }}
            >
              <div style={{ display: "flex", justifyContent: "space-between", alignItems: "center", gap: "0.5rem" }}>
                <div style={{ display: "flex", alignItems: "center", gap: "0.5rem" }}>
                  <span className="badge badge-public" style={{ minWidth: 36, textAlign: "center" }}>
                    Node {node.displayOrder}
                  </span>
                  <span style={{ fontSize: "0.75rem", color: "var(--text-muted)" }}>{node.id}</span>
                </div>
                {!readOnly && (
                  <button
                    className="btn btn-outline btn-sm"
                    onClick={() => removeNode(node.id)}
                    style={{ color: "var(--danger)", borderColor: "rgba(224,85,85,0.35)" }}
                  >
                    Remove
                  </button>
                )}
              </div>

              {readOnly ? (
                <>
                  <div style={{ fontWeight: 600, fontSize: "0.9rem" }}>{node.title}</div>
                  <div style={{ fontSize: "0.8rem", color: "var(--text-muted)" }}>{node.description || "No description"}</div>
                  <div style={{ fontSize: "0.75rem" }}>
                    Agent: <strong>{node.agentName || "Unassigned"}</strong>
                  </div>
                  <div style={{ fontSize: "0.75rem", color: "var(--text-muted)" }}>
                    Next: {outgoingTargets.length > 0 ? outgoingTargets.join(", ") : "Terminal node"}
                  </div>
                </>
              ) : (
                <>
                  <div style={{ display: "grid", gridTemplateColumns: "1fr 90px", gap: "0.5rem" }}>
                    <input
                      value={node.title}
                      onChange={(event) => updateNode(node.id, { title: event.target.value })}
                      placeholder="Node title"
                      style={{ marginBottom: 0 }}
                    />
                    <input
                      type="number"
                      min={1}
                      value={node.displayOrder}
                      onChange={(event) => updateNode(node.id, { displayOrder: Number(event.target.value) || 1 })}
                      placeholder="Order"
                      style={{ marginBottom: 0 }}
                    />
                  </div>
                  <textarea
                    value={node.description}
                    onChange={(event) => updateNode(node.id, { description: event.target.value })}
                    placeholder="What this node does"
                    style={{ minHeight: 72, marginBottom: 0 }}
                  />
                  <select
                    value={node.agentId}
                    onChange={(event) => {
                      const nextAgent = agents.find((agent) => agent.id === Number(event.target.value));
                      if (!nextAgent) return;
                      updateNode(node.id, { agentId: nextAgent.id, agentName: nextAgent.name });
                    }}
                    style={{ marginBottom: 0 }}
                  >
                    {agents.map((agent) => (
                      <option key={agent.id} value={agent.id}>
                        {agent.icon} {agent.name}
                      </option>
                    ))}
                  </select>
                  <textarea
                    value={node.promptOverride ?? ""}
                    onChange={(event) => updateNode(node.id, { promptOverride: event.target.value || null })}
                    placeholder="Prompt override (optional)"
                    style={{ minHeight: 72, marginBottom: 0 }}
                  />

                  <div>
                    <div style={{ fontSize: "0.75rem", fontWeight: 600, marginBottom: "0.35rem" }}>Next nodes</div>
                    <div style={{ display: "grid", gap: "0.35rem" }}>
                      {orderedNodes.filter((candidate) => candidate.id !== node.id).map((candidate) => (
                        <label
                          key={candidate.id}
                          style={{
                            display: "flex",
                            alignItems: "center",
                            gap: "0.5rem",
                            fontSize: "0.8rem",
                            color: "var(--text-muted)",
                          }}
                        >
                          <input
                            type="checkbox"
                            checked={isChecked(node.id, candidate.id)}
                            onChange={() => toggleEdge(node.id, candidate.id)}
                            style={{ width: "auto", margin: 0 }}
                          />
                          <span>{candidate.title || candidate.agentName || candidate.id}</span>
                        </label>
                      ))}
                    </div>
                  </div>
                </>
              )}
            </div>
          );
        })}
      </div>
    </div>
  );
}