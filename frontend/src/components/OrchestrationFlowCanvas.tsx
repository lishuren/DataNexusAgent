import { createContext, memo, useCallback, useContext, useEffect, useMemo, useRef, useState } from "react";
import {
  ReactFlow,
  Background,
  Controls,
  Handle,
  MarkerType,
  Panel,
  Position,
  addEdge,
  applyEdgeChanges,
  applyNodeChanges,
  type Connection,
  type Edge,
  type EdgeChange,
  type Node,
  type NodeChange,
} from "@xyflow/react";
import "@xyflow/react/dist/style.css";
import type { Agent, OrchestrationGraph, OrchestrationGraphNode } from "@/types/api";
import { createGraphNode, validateGraph } from "@/utils/orchestrationGraph";

// ─── Types ────────────────────────────────────────────────────────────────────

interface FlowNodeData extends Record<string, unknown> {
  graphNode: OrchestrationGraphNode;
}

type FlowNode = Node<FlowNodeData, "agentNode">;

// ─── Model ↔ React Flow conversion ────────────────────────────────────────────

function toFlowNode(n: OrchestrationGraphNode): FlowNode {
  return {
    id: n.id,
    type: "agentNode",
    position: { x: n.positionX, y: n.positionY },
    data: { graphNode: n },
  };
}

function toRfEdge(e: OrchestrationGraph["edges"][number]): Edge {
  return {
    id: e.id,
    source: e.sourceNodeId,
    target: e.targetNodeId,
    type: "smoothstep",
    markerEnd: { type: MarkerType.ArrowClosed, color: "var(--primary)" },
    style: { stroke: "var(--primary)", strokeWidth: 1.5 },
    deletable: true,
  };
}

function rfToGraph(nodes: FlowNode[], edges: Edge[]): OrchestrationGraph {
  return {
    nodes: nodes.map((n) => ({
      ...n.data.graphNode,
      positionX: Math.round(n.position.x),
      positionY: Math.round(n.position.y),
    })),
    edges: edges.map((e) => ({
      id: e.id,
      sourceNodeId: e.source,
      targetNodeId: e.target,
    })),
  };
}

// ─── Context (shared by all custom nodes) ─────────────────────────────────────

interface FlowCtxValue {
  agents: Agent[];
  readOnly: boolean;
  onUpdate: (id: string, patch: Partial<OrchestrationGraphNode>) => void;
  onRemove: (id: string) => void;
}

const FlowCtx = createContext<FlowCtxValue>({
  agents: [],
  readOnly: false,
  onUpdate: () => {},
  onRemove: () => {},
});

// ─── Custom node component ─────────────────────────────────────────────────────

type RawNodeProps = { id: string; data: unknown; selected: boolean };

const AgentFlowNode = memo(function AgentFlowNode({ data, selected }: RawNodeProps) {
  const { agents, readOnly, onUpdate, onRemove } = useContext(FlowCtx);
  const node = (data as FlowNodeData).graphNode;

  // Prevent React Flow drag from starting when the user interacts with inputs
  const stop = (e: React.SyntheticEvent) => e.stopPropagation();

  return (
    <div
      style={{
        background: "var(--surface)",
        border: `1px solid ${selected ? "var(--primary)" : "var(--border)"}`,
        borderRadius: "0.65rem",
        padding: "0.65rem",
        width: 240,
        display: "flex",
        flexDirection: "column",
        gap: "0.4rem",
        boxShadow: selected ? "0 0 0 2px rgba(245,166,35,0.3)" : "none",
        cursor: readOnly ? "default" : "grab",
      }}
    >
      {/* Incoming connection handle (top) */}
      <Handle
        type="target"
        position={Position.Top}
        style={{
          background: "var(--primary)",
          border: "2px solid var(--bg)",
          width: 10,
          height: 10,
          top: -6,
        }}
      />

      {/* Header: order badge + remove button */}
      <div style={{ display: "flex", justifyContent: "space-between", alignItems: "center" }}>
        <span className="badge badge-public" style={{ fontSize: "0.7rem", minWidth: 32, textAlign: "center" }}>
          #{node.displayOrder}
        </span>
        {!readOnly && (
          <button
            className="btn btn-outline btn-sm"
            onMouseDown={stop}
            onClick={(e) => { stop(e); onRemove(node.id); }}
            style={{
              color: "var(--danger)",
              borderColor: "rgba(224,85,85,0.35)",
              fontSize: "0.7rem",
              padding: "1px 7px",
              lineHeight: 1.4,
            }}
          >
            ✕
          </button>
        )}
      </div>

      {/* Body: read-only view or edit fields */}
      {readOnly ? (
        <>
          <div style={{ fontWeight: 600, fontSize: "0.85rem", wordBreak: "break-word" }}>
            {node.title}
          </div>
          <div style={{ fontSize: "0.75rem", color: "var(--text-muted)" }}>
            {agents.find((a) => a.id === node.agentId)?.icon ?? "🤖"} {node.agentName}
          </div>
          {node.description && (
            <div style={{ fontSize: "0.75rem", color: "var(--text-muted)", wordBreak: "break-word" }}>
              {node.description}
            </div>
          )}
        </>
      ) : (
        <>
          <input
            value={node.title}
            onChange={(e) => onUpdate(node.id, { title: e.target.value })}
            onMouseDown={stop}
            placeholder="Node title"
            style={{ marginBottom: 0, fontSize: "0.8rem" }}
          />
          <select
            value={node.agentId}
            onChange={(e) => {
              const agent = agents.find((a) => a.id === Number(e.target.value));
              if (agent) onUpdate(node.id, { agentId: agent.id, agentName: agent.name });
            }}
            onMouseDown={stop}
            style={{ marginBottom: 0, fontSize: "0.8rem" }}
          >
            {agents.map((a) => (
              <option key={a.id} value={a.id}>{a.icon} {a.name}</option>
            ))}
          </select>
          <textarea
            value={node.description}
            onChange={(e) => onUpdate(node.id, { description: e.target.value })}
            onMouseDown={stop}
            placeholder="What this node does"
            style={{ marginBottom: 0, minHeight: 52, fontSize: "0.78rem", resize: "vertical" }}
          />
          <textarea
            value={node.promptOverride ?? ""}
            onChange={(e) => onUpdate(node.id, { promptOverride: e.target.value || null })}
            onMouseDown={stop}
            placeholder="Prompt override (optional)"
            style={{ marginBottom: 0, minHeight: 42, fontSize: "0.78rem", resize: "vertical" }}
          />
        </>
      )}

      {/* Outgoing connection handle (bottom) */}
      <Handle
        type="source"
        position={Position.Bottom}
        style={{
          background: "var(--primary)",
          border: "2px solid var(--bg)",
          width: 10,
          height: 10,
          bottom: -6,
        }}
      />
    </div>
  );
});

// Defined outside the parent component to prevent React Flow from remounting nodes on each render
// eslint-disable-next-line @typescript-eslint/no-explicit-any
const NODE_TYPES = { agentNode: AgentFlowNode as React.ComponentType<any> };

// ─── Main canvas component ─────────────────────────────────────────────────────

interface OrchestrationFlowCanvasProps {
  graph: OrchestrationGraph;
  agents: Agent[];
  readOnly?: boolean;
  onChange?: (graph: OrchestrationGraph) => void;
}

export function OrchestrationFlowCanvas({
  graph,
  agents,
  readOnly = false,
  onChange,
}: OrchestrationFlowCanvasProps) {
  const [rfNodes, setRfNodes] = useState<FlowNode[]>(() => graph.nodes.map(toFlowNode));
  const [rfEdges, setRfEdges] = useState<Edge[]>(() => graph.edges.map(toRfEdge));

  // Keep stable refs so callbacks don't become stale
  const rfNodesRef = useRef(rfNodes);
  rfNodesRef.current = rfNodes;
  const rfEdgesRef = useRef(rfEdges);
  rfEdgesRef.current = rfEdges;

  // In read-only mode, stay in sync with the graph prop (e.g., after an external save)
  useEffect(() => {
    if (readOnly) {
      setRfNodes(graph.nodes.map(toFlowNode));
      setRfEdges(graph.edges.map(toRfEdge));
    }
  }, [readOnly, graph]);

  // Derive validation issues from current local canvas state
  const issues = useMemo(() => validateGraph(rfToGraph(rfNodes, rfEdges)), [rfNodes, rfEdges]);

  // ─── Node / edge callbacks ─────────────────────────────────────────────────

  const onUpdate = useCallback((id: string, patch: Partial<OrchestrationGraphNode>) => {
    setRfNodes((prev) => {
      const next = prev.map((n) =>
        n.id === id
          ? { ...n, data: { graphNode: { ...n.data.graphNode, ...patch, isEdited: true } } }
          : n,
      );
      onChange?.(rfToGraph(next, rfEdgesRef.current));
      return next;
    });
  }, [onChange]);

  const onRemove = useCallback((id: string) => {
    const nextNodes = rfNodesRef.current.filter((n) => n.id !== id);
    const nextEdges = rfEdgesRef.current.filter((e) => e.source !== id && e.target !== id);
    setRfNodes(nextNodes);
    setRfEdges(nextEdges);
    onChange?.(rfToGraph(nextNodes, nextEdges));
  }, [onChange]);

  const handleNodesChange = useCallback((changes: NodeChange[]) => {
    const removedIds = changes
      .filter((c) => c.type === "remove")
      .map((c) => (c as { id: string }).id);

    if (removedIds.length > 0) {
      // Node deleted via keyboard — also prune orphaned edges
      const nextNodes = applyNodeChanges(changes, rfNodesRef.current) as FlowNode[];
      const nextEdges = rfEdgesRef.current.filter(
        (e) => !removedIds.includes(e.source) && !removedIds.includes(e.target),
      );
      setRfNodes(nextNodes);
      setRfEdges(nextEdges);
      onChange?.(rfToGraph(nextNodes, nextEdges));
    } else {
      // Position/select/dimension changes — update local state only; onChange fires on drag stop
      setRfNodes((prev) => applyNodeChanges(changes, prev) as FlowNode[]);
    }
  }, [onChange]);

  const handleEdgesChange = useCallback((changes: EdgeChange[]) => {
    const hasRemoval = changes.some((c) => c.type === "remove");
    setRfEdges((prev) => {
      const next = applyEdgeChanges(changes, prev);
      if (hasRemoval) {
        onChange?.(rfToGraph(rfNodesRef.current, next));
      }
      return next;
    });
  }, [onChange]);

  const handleConnect = useCallback((connection: Connection) => {
    if (!connection.source || !connection.target) return;
    if (connection.source === connection.target) return;
    // Avoid duplicate edges
    if (rfEdgesRef.current.some((e) => e.source === connection.source && e.target === connection.target)) return;

    const edgeId = `edge-${connection.source}-${connection.target}`;
    setRfEdges((prev) => {
      const next = addEdge(
        {
          ...connection,
          id: edgeId,
          type: "smoothstep",
          markerEnd: { type: MarkerType.ArrowClosed, color: "var(--primary)" },
          style: { stroke: "var(--primary)", strokeWidth: 1.5 },
          deletable: true,
        },
        prev,
      );
      onChange?.(rfToGraph(rfNodesRef.current, next));
      return next;
    });
  }, [onChange]);

  // Persist positions only once drag ends (not on every frame)
  const handleNodeDragStop = useCallback(() => {
    onChange?.(rfToGraph(rfNodesRef.current, rfEdgesRef.current));
  }, [onChange]);

  const handleAddNode = useCallback(() => {
    const agent = agents[0];
    if (!agent) return;
    const lastNode = rfNodesRef.current[rfNodesRef.current.length - 1];
    const newModelNode: OrchestrationGraphNode = {
      ...createGraphNode(agent, rfNodesRef.current.length + 1),
      positionX: lastNode ? Math.round(lastNode.position.x) + 210 : 120,
      positionY: lastNode ? Math.round(lastNode.position.y) : 120,
    };
    const nextNodes = [...rfNodesRef.current, toFlowNode(newModelNode)];
    setRfNodes(nextNodes);
    onChange?.(rfToGraph(nextNodes, rfEdgesRef.current));
  }, [agents, onChange]);

  const ctxValue = useMemo<FlowCtxValue>(
    () => ({ agents, readOnly, onUpdate, onRemove }),
    [agents, readOnly, onUpdate, onRemove],
  );

  // ─── Render ───────────────────────────────────────────────────────────────

  return (
    <FlowCtx.Provider value={ctxValue}>
      <div style={{ display: "flex", flexDirection: "column", gap: "0.75rem" }}>
        {issues.length > 0 && (
          <div
            style={{
              padding: "0.6rem 0.75rem",
              borderRadius: "0.5rem",
              border: "1px solid rgba(224,85,85,0.35)",
              background: "rgba(224,85,85,0.08)",
              fontSize: "0.8rem",
              color: "var(--danger)",
            }}
          >
            {issues.map((issue) => (
              <div key={issue}>{issue}</div>
            ))}
          </div>
        )}

        <div
          style={{
            height: "min(75vh, 680px)",
            borderRadius: "0.65rem",
            border: "1px solid var(--border)",
            overflow: "hidden",
            // Dark-theme overrides for React Flow's CSS variables
            ["--xy-background-color-default" as string]: "var(--bg)",
            ["--xy-background-pattern-dots-color-default" as string]: "var(--border)",
            ["--xy-controls-button-background-color-default" as string]: "var(--surface)",
            ["--xy-controls-button-border-color-default" as string]: "var(--border)",
            ["--xy-controls-button-color-default" as string]: "var(--text)",
            ["--xy-controls-button-background-color-hover-default" as string]: "var(--border)",
            ["--xy-attribution-background-color-default" as string]: "rgba(44,31,20,0.8)",
          }}
        >
          <ReactFlow
            nodes={rfNodes}
            edges={rfEdges}
            nodeTypes={NODE_TYPES}
            onNodesChange={handleNodesChange}
            onEdgesChange={handleEdgesChange}
            onConnect={handleConnect}
            onNodeDragStop={handleNodeDragStop}
            fitView
            fitViewOptions={{ padding: 0.25 }}
            nodesDraggable={!readOnly}
            nodesConnectable={!readOnly}
            elementsSelectable={!readOnly}
            colorMode="dark"
          >
            <Background color="var(--border)" gap={22} size={1.5} />
            <Controls showInteractive={false} />
            {!readOnly && agents.length > 0 && (
              <Panel position="top-right">
                <button
                  className="btn btn-outline btn-sm"
                  onClick={handleAddNode}
                  style={{ pointerEvents: "all" }}
                >
                  + Add Node
                </button>
              </Panel>
            )}
          </ReactFlow>
        </div>

        {!readOnly && (
          <div style={{ fontSize: "0.78rem", color: "var(--text-muted)" }}>
            Drag nodes to reposition · Draw edges by dragging from the bottom handle to a top handle · Select an edge or node and press <kbd style={{ padding: "0 4px", border: "1px solid var(--border)", borderRadius: 3 }}>Delete</kbd> to remove
          </div>
        )}
      </div>
    </FlowCtx.Provider>
  );
}
