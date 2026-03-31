import { useState } from "react";
import type {
  Agent,
  ExecutionMode,
  Orchestration,
  OrchestrationGraph,
  OrchestrationStep,
  OrchestrationWorkflowKind,
  ProcessingResult,
} from "@/types/api";
import {
  approveOrchestration,
  rejectOrchestration,
  resetOrchestration,
  updateOrchestration,
  deleteOrchestration,
  runOrchestrationStream,
  publishOrchestration,
  unpublishOrchestration,
  cloneOrchestration,
} from "@/services/api";
import { LiveRunBox } from "@/components/LiveRunBox";
import { OrchestrationGraphEditor } from "@/components/OrchestrationGraphEditor";
import {
  buildGraphFromSteps,
  cloneGraph,
  graphToSteps,
} from "@/utils/orchestrationGraph";
import { applyProcessingStreamEvent, createLiveRunState, type LiveRunState } from "@/utils/processingStream";

interface OrchestrationReviewProps {
  orchestration: Orchestration;
  agents: Agent[];
  currentUserId?: string;
  onUpdated: () => void;
  onResult?: (result: ProcessingResult) => void;
}

const STATUS_BADGE: Record<string, { className: string; label: string }> = {
  Draft: { className: "badge badge-private", label: "Draft" },
  Approved: { className: "badge badge-public", label: "Approved" },
  Rejected: { className: "badge badge-private", label: "Rejected" },
  Running: { className: "badge badge-public", label: "Running…" },
  Completed: { className: "badge badge-public", label: "Completed" },
  Failed: { className: "badge badge-private", label: "Failed" },
};

const WORKFLOW_BADGE: Record<OrchestrationWorkflowKind, { className: string; label: string }> = {
  Structured: { className: "badge badge-public", label: "Structured" },
  Graph: { className: "badge badge-private", label: "Graph DAG" },
};

const EXECUTION_MODE_OPTIONS: Array<{ value: ExecutionMode; label: string }> = [
  { value: "Sequential", label: "Sequential" },
  { value: "Concurrent", label: "Concurrent" },
  { value: "Handoff", label: "Handoff" },
  { value: "GroupChat", label: "Group Chat" },
];

function describeExecutionMode(
  workflowKind: OrchestrationWorkflowKind,
  mode: ExecutionMode,
  triageStepNumber: number,
  groupChatMaxIterations: number,
): string {
  if (workflowKind === "Graph") {
    return "This orchestration runs as a DAG. Node edges and join barriers define branching and merge order directly, so structured execution modes do not apply.";
  }

  switch (mode) {
    case "Concurrent":
      return "All orchestration steps fan out in parallel and their outputs are merged before completion.";
    case "Handoff":
      return `Step ${triageStepNumber} acts as the triage agent and can hand work off to the remaining steps.`;
    case "GroupChat":
      return `All steps participate in a round-robin discussion capped at ${groupChatMaxIterations} turns.`;
    default:
      return "Steps run one after another, passing output along the chain.";
  }
}

export function OrchestrationReview({
  orchestration: orch,
  agents,
  currentUserId,
  onUpdated,
  onResult,
}: OrchestrationReviewProps) {
  const [editingWorkflowKind, setEditingWorkflowKind] = useState<OrchestrationWorkflowKind | null>(null);
  const [editingSteps, setEditingSteps] = useState<OrchestrationStep[] | null>(null);
  const [editingGraph, setEditingGraph] = useState<OrchestrationGraph | null>(null);
  const [editingExecutionMode, setEditingExecutionMode] = useState<ExecutionMode | null>(null);
  const [editingTriageStepNumber, setEditingTriageStepNumber] = useState<number | null>(null);
  const [editingGroupChatMaxIterations, setEditingGroupChatMaxIterations] = useState<number | null>(null);
  const [editingName, setEditingName] = useState(orch.name);
  const [inputSource, setInputSource] = useState("");
  const [loading, setLoading] = useState(false);
  const [liveRun, setLiveRun] = useState<LiveRunState | null>(null);
  const [msg, setMsg] = useState("");

  const isOwner = orch.ownerId === currentUserId;
  const isDraft = orch.status === "Draft";
  const isApproved = orch.status === "Approved";
  const isEditing = editingWorkflowKind !== null;
  const isStructuredEditing = editingWorkflowKind === "Structured" && editingSteps !== null;
  const isGraphEditing = editingWorkflowKind === "Graph" && editingGraph !== null;
  const workflowKind = editingWorkflowKind ?? orch.workflowKind;
  const executionMode = editingExecutionMode ?? orch.executionMode;
  const triageStepNumber = editingTriageStepNumber ?? orch.triageStepNumber;
  const groupChatMaxIterations = editingGroupChatMaxIterations ?? orch.groupChatMaxIterations;
  const graph = isGraphEditing ? editingGraph : orch.graph;
  const steps = workflowKind === "Graph" && graph
    ? graphToSteps(graph)
    : (editingSteps ?? orch.steps);
  const badge = STATUS_BADGE[orch.status] ?? { className: "badge", label: orch.status };
  const workflowBadge = WORKFLOW_BADGE[workflowKind];

  const getAgent = (id: number) => agents.find((a) => a.id === id);

  const startEditing = () => {
    setEditingName(orch.name);
    setMsg("");

    if (orch.workflowKind === "Graph") {
      setEditingWorkflowKind("Graph");
      setEditingGraph(cloneGraph(orch.graph ?? buildGraphFromSteps(orch.steps)));
      setEditingSteps(null);
      setEditingExecutionMode(orch.executionMode);
      setEditingTriageStepNumber(orch.triageStepNumber);
      setEditingGroupChatMaxIterations(orch.groupChatMaxIterations);
      return;
    }

    setEditingWorkflowKind("Structured");
    setEditingSteps(orch.steps.map((step) => ({ ...step })));
    setEditingGraph(null);
    setEditingExecutionMode(orch.executionMode);
    setEditingTriageStepNumber(orch.triageStepNumber);
    setEditingGroupChatMaxIterations(orch.groupChatMaxIterations);
  };

  const startGraphEditing = () => {
    setEditingName(orch.name);
    setMsg("");
    setEditingWorkflowKind("Graph");
    setEditingGraph(cloneGraph(orch.graph ?? buildGraphFromSteps(editingSteps ?? orch.steps)));
    setEditingSteps(null);
    setEditingExecutionMode(editingExecutionMode ?? orch.executionMode);
    setEditingTriageStepNumber(editingTriageStepNumber ?? orch.triageStepNumber);
    setEditingGroupChatMaxIterations(editingGroupChatMaxIterations ?? orch.groupChatMaxIterations);
  };

  const cancelEditing = () => {
    setEditingWorkflowKind(null);
    setEditingSteps(null);
    setEditingGraph(null);
    setEditingExecutionMode(null);
    setEditingTriageStepNumber(null);
    setEditingGroupChatMaxIterations(null);
    setMsg("");
  };

  const updateStep = (idx: number, patch: Partial<OrchestrationStep>) => {
    if (!editingSteps) return;
    const next = [...editingSteps];
    const step = next[idx];
    if (!step) return;
    next[idx] = { ...step, ...patch, isEdited: true };
    setEditingSteps(next);
  };

  const swapAgent = (idx: number, newAgentId: number) => {
    const agent = getAgent(newAgentId);
    if (!agent || !editingSteps) return;
    updateStep(idx, { agentId: newAgentId, agentName: agent.name });
  };

  const removeStep = (idx: number) => {
    if (!editingSteps) return;
    const next = editingSteps.filter((_, i) => i !== idx)
      .map((step, index) => ({ ...step, stepNumber: index + 1 }));
    setEditingSteps(next);
  };

  const addStep = () => {
    if (!editingSteps) return;
    const firstAgent = agents[0];
    if (!firstAgent) return;
    const newStep: OrchestrationStep = {
      stepNumber: editingSteps.length + 1,
      title: "New Step",
      description: "",
      agentId: firstAgent.id,
      agentName: firstAgent.name,
      isEdited: true,
      promptOverride: null,
      parameters: null,
    };
    setEditingSteps([...editingSteps, newStep]);
  };

  const saveEdits = async () => {
    const nextWorkflowKind = editingWorkflowKind ?? orch.workflowKind;
    const nextGraph = nextWorkflowKind === "Graph" ? (editingGraph ?? orch.graph) : null;
    const nextSteps = nextWorkflowKind === "Graph"
      ? (nextGraph ? graphToSteps(nextGraph) : [])
      : editingSteps;

    if (nextWorkflowKind !== "Graph" && (!nextSteps || nextSteps.length < 1)) {
      setMsg("At least one step is required.");
      return;
    }

    if (nextWorkflowKind === "Graph" && !nextGraph) {
      setMsg("Graph data is required.");
      return;
    }

    const nextExecutionMode = nextWorkflowKind === "Graph" ? "Sequential" : executionMode;
    const nextTriageStepNumber = nextWorkflowKind === "Graph"
      ? 1
      : Math.max(1, Math.min(triageStepNumber, Math.max(1, nextSteps?.length ?? 1)));
    const nextGroupChatMaxIterations = nextWorkflowKind === "Graph"
      ? 10
      : Math.max(2, Math.min(50, groupChatMaxIterations));

    setLoading(true);
    try {
      await updateOrchestration(orch.id, {
        name: editingName,
        steps: nextSteps ?? undefined,
        workflowKind: nextWorkflowKind,
        graph: nextGraph,
        enableSelfCorrection: orch.enableSelfCorrection,
        maxCorrectionAttempts: orch.maxCorrectionAttempts,
        executionMode: nextExecutionMode,
        triageStepNumber: nextTriageStepNumber,
        groupChatMaxIterations: nextGroupChatMaxIterations,
      });
      cancelEditing();
      setMsg("Changes saved.");
      onUpdated();
    } catch (e) {
      setMsg(e instanceof Error ? e.message : String(e));
    }
    setLoading(false);
  };

  const handleApprove = async () => {
    setLoading(true);
    try {
      await approveOrchestration(orch.id);
      setMsg("Orchestration approved.");
      onUpdated();
    } catch (e) {
      setMsg(e instanceof Error ? e.message : String(e));
    }
    setLoading(false);
  };

  const handleReject = async () => {
    const reason = window.prompt("Rejection reason (optional):");
    setLoading(true);
    try {
      await rejectOrchestration(orch.id, reason ?? undefined);
      setMsg("Orchestration rejected.");
      onUpdated();
    } catch (e) {
      setMsg(e instanceof Error ? e.message : String(e));
    }
    setLoading(false);
  };

  const handleReset = async () => {
    setLoading(true);
    try {
      await resetOrchestration(orch.id);
      setMsg("Reset to Draft.");
      onUpdated();
    } catch (e) {
      setMsg(e instanceof Error ? e.message : String(e));
    }
    setLoading(false);
  };

  const handleRun = async () => {
    if (!inputSource.trim()) {
      setMsg("Input source is required to run.");
      return;
    }

    setLoading(true);
    setLiveRun(createLiveRunState(`Starting orchestration '${orch.name}'…`));
    try {
      const result = await runOrchestrationStream(orch.id, inputSource, (event) => {
        setLiveRun((prev) => applyProcessingStreamEvent(prev ?? createLiveRunState(), event));
      });
      setMsg(result.success ? "Orchestration completed!" : `Failed: ${result.message}`);
      onResult?.(result);
      onUpdated();
    } catch (e) {
      setMsg(e instanceof Error ? e.message : String(e));
      setLiveRun((prev) => prev
        ? {
            ...prev,
            active: false,
            statusLines: [...prev.statusLines, "Run failed."].slice(-6),
          }
        : null);
    }
    setLiveRun((prev) => prev ? { ...prev, active: false } : prev);
    setLoading(false);
  };

  const handleDelete = async () => {
    setLoading(true);
    try {
      await deleteOrchestration(orch.id);
      onUpdated();
    } catch (e) {
      setMsg(e instanceof Error ? e.message : String(e));
    }
    setLoading(false);
  };

  const handlePublish = async () => {
    setLoading(true);
    try {
      await publishOrchestration(orch.id);
      setMsg("Published to marketplace.");
      onUpdated();
    } catch (e) {
      setMsg(e instanceof Error ? e.message : String(e));
    }
    setLoading(false);
  };

  const handleUnpublish = async () => {
    setLoading(true);
    try {
      await unpublishOrchestration(orch.id);
      setMsg("Moved back to private.");
      onUpdated();
    } catch (e) {
      setMsg(e instanceof Error ? e.message : String(e));
    }
    setLoading(false);
  };

  const handleClone = async () => {
    const name = window.prompt("Clone name:", orch.name);
    if (!name?.trim()) return;
    setLoading(true);
    try {
      await cloneOrchestration(orch.id, name.trim());
      setMsg("Cloned successfully.");
      onUpdated();
    } catch (e) {
      setMsg(e instanceof Error ? e.message : String(e));
    }
    setLoading(false);
  };

  return (
    <div className="card" style={{ borderLeft: "3px solid var(--primary)" }}>
      <div style={{ display: "flex", justifyContent: "space-between", alignItems: "center", marginBottom: "0.75rem", gap: "0.75rem" }}>
        <div>
          <h3 style={{ margin: 0 }}>
            🗂️ {isEditing ? (
              <input
                value={editingName}
                onChange={(e) => setEditingName(e.target.value)}
                style={{ fontWeight: 600, fontSize: "inherit", width: "300px" }}
              />
            ) : orch.name}
          </h3>
          <div style={{ fontSize: "0.8rem", color: "var(--text-muted)", marginTop: "0.25rem" }}>
            Goal: {orch.goal}
          </div>
        </div>
        <div style={{ display: "flex", alignItems: "center", gap: "0.35rem", flexWrap: "wrap", justifyContent: "flex-end" }}>
          <span className={workflowBadge.className}>{workflowBadge.label}</span>
          <span className={badge.className}>{badge.label}</span>
        </div>
      </div>

      {orch.plannerNotes && (
        <div style={{
          padding: "0.5rem 0.75rem",
          background: "rgba(var(--primary-rgb, 99,102,241),0.05)",
          border: "1px solid var(--border)",
          borderRadius: "0.5rem",
          marginBottom: "0.75rem",
          fontSize: "0.8rem",
        }}>
          <strong>Planner notes:</strong> {orch.plannerNotes}
          {orch.plannerModel && (
            <span style={{ color: "var(--text-muted)", marginLeft: "0.5rem" }}>
              (model: {orch.plannerModel})
            </span>
          )}
        </div>
      )}

      <div style={{
        padding: "0.65rem 0.75rem",
        border: "1px solid var(--border)",
        borderRadius: "0.5rem",
        background: "rgba(245, 166, 35, 0.05)",
        marginBottom: "0.75rem",
      }}>
        <div style={{ display: "flex", justifyContent: "space-between", gap: "0.75rem", flexWrap: "wrap", alignItems: "flex-start" }}>
          <div>
            <div style={{ fontWeight: 600, fontSize: "0.8rem", marginBottom: "0.2rem" }}>
              {workflowKind === "Graph" ? "Execution Topology" : "Execution Mode"}
            </div>
            <div style={{ fontSize: "0.78rem", color: "var(--text-muted)" }}>
              {describeExecutionMode(workflowKind, executionMode, triageStepNumber, groupChatMaxIterations)}
            </div>
          </div>
          <div style={{ display: "flex", gap: "0.35rem", flexWrap: "wrap" }}>
            {workflowKind === "Graph" ? (
              <span className="badge badge-private" style={{ textTransform: "none" }}>
                {(graph?.nodes.length ?? steps.length)} nodes
              </span>
            ) : (
              <>
                <span className="badge badge-private" style={{ textTransform: "none" }}>
                  {EXECUTION_MODE_OPTIONS.find((option) => option.value === executionMode)?.label ?? executionMode}
                </span>
                {executionMode === "Handoff" && (
                  <span className="badge badge-private" style={{ textTransform: "none" }}>
                    Triage step {triageStepNumber}
                  </span>
                )}
                {executionMode === "GroupChat" && (
                  <span className="badge badge-private" style={{ textTransform: "none" }}>
                    {groupChatMaxIterations} turns
                  </span>
                )}
              </>
            )}
          </div>
        </div>

        {isEditing && workflowKind !== "Graph" && (
          <div style={{ display: "grid", gridTemplateColumns: "repeat(auto-fit, minmax(220px, 1fr))", gap: "0.75rem", marginTop: "0.75rem" }}>
            <div>
              <label style={{ fontSize: "0.8rem", color: "var(--text-muted)", display: "block", marginBottom: 4 }}>
                Execution Mode
              </label>
              <select
                value={executionMode}
                onChange={(e) => setEditingExecutionMode(e.target.value as ExecutionMode)}
                style={{ marginBottom: 0 }}
              >
                {EXECUTION_MODE_OPTIONS.map((option) => (
                  <option key={option.value} value={option.value}>{option.label}</option>
                ))}
              </select>
            </div>

            {executionMode === "Handoff" && (
              <div>
                <label style={{ fontSize: "0.8rem", color: "var(--text-muted)", display: "block", marginBottom: 4 }}>
                  Triage Step
                </label>
                <input
                  type="number"
                  min={1}
                  max={Math.max(1, steps.length)}
                  value={triageStepNumber}
                  onChange={(e) => setEditingTriageStepNumber(Math.max(1, Number(e.target.value) || 1))}
                  style={{ marginBottom: 0 }}
                />
              </div>
            )}

            {executionMode === "GroupChat" && (
              <div>
                <label style={{ fontSize: "0.8rem", color: "var(--text-muted)", display: "block", marginBottom: 4 }}>
                  Max Chat Turns
                </label>
                <input
                  type="number"
                  min={2}
                  max={50}
                  value={groupChatMaxIterations}
                  onChange={(e) => setEditingGroupChatMaxIterations(Math.max(2, Math.min(50, Number(e.target.value) || 10)))}
                  style={{ marginBottom: 0 }}
                />
              </div>
            )}
          </div>
        )}
      </div>

      {workflowKind === "Graph" && graph ? (
        <div style={{ marginBottom: "0.75rem" }}>
          <OrchestrationGraphEditor
            graph={graph}
            agents={agents}
            readOnly={!isGraphEditing}
            onChange={isGraphEditing ? setEditingGraph : undefined}
          />
        </div>
      ) : (
        <div style={{ display: "flex", flexDirection: "column", gap: "0.5rem", marginBottom: "0.75rem" }}>
          {steps.map((step, idx) => {
            const agent = getAgent(step.agentId);
            return (
              <div key={`${step.stepNumber}-${step.agentId}-${idx}`} style={{
                display: "flex",
                alignItems: "flex-start",
                gap: "0.75rem",
                padding: "0.6rem 0.75rem",
                background: "var(--surface)",
                border: "1px solid var(--border)",
                borderRadius: "0.5rem",
              }}>
                <div style={{
                  width: 28,
                  height: 28,
                  borderRadius: "50%",
                  background: "var(--primary)",
                  color: "#fff",
                  display: "flex",
                  alignItems: "center",
                  justifyContent: "center",
                  fontSize: "0.8rem",
                  fontWeight: 700,
                  flexShrink: 0,
                }}>
                  {step.stepNumber}
                </div>
                <div style={{ flex: 1 }}>
                  <div style={{ fontWeight: 600, fontSize: "0.85rem" }}>
                    {isStructuredEditing ? (
                      <input
                        value={step.title}
                        onChange={(e) => updateStep(idx, { title: e.target.value })}
                        style={{ fontWeight: 600, fontSize: "inherit", width: "100%" }}
                      />
                    ) : (
                      <>
                        {step.title}
                        {step.isEdited && (
                          <span style={{ color: "var(--primary)", fontSize: "0.7rem", marginLeft: "0.4rem" }}>edited</span>
                        )}
                      </>
                    )}
                  </div>
                  <div style={{ fontSize: "0.8rem", color: "var(--text-muted)" }}>{step.description}</div>
                  <div style={{ fontSize: "0.75rem", marginTop: "0.25rem" }}>
                    Agent: {agent?.icon ?? "🤖"} <strong>{step.agentName}</strong>
                  </div>

                  {isStructuredEditing && (
                    <div style={{ marginTop: "0.5rem" }}>
                      <div style={{ display: "flex", gap: "0.5rem", alignItems: "center", marginBottom: "0.3rem" }}>
                        <select
                          value={step.agentId}
                          onChange={(e) => swapAgent(idx, Number(e.target.value))}
                          style={{ fontSize: "0.8rem", padding: "2px 6px" }}
                        >
                          {agents.map((a) => (
                            <option key={a.id} value={a.id}>{a.icon} {a.name}</option>
                          ))}
                        </select>
                        <button
                          className="btn btn-sm btn-outline btn-outline-danger"
                          onClick={() => removeStep(idx)}
                          style={{ fontSize: "0.7rem", padding: "2px 8px" }}
                        >
                          Remove
                        </button>
                      </div>
                      <textarea
                        placeholder="Prompt override (leave empty to use agent's default prompt)"
                        value={step.promptOverride ?? ""}
                        onChange={(e) => updateStep(idx, { promptOverride: e.target.value || null })}
                        style={{ fontSize: "0.75rem", minHeight: "50px", width: "100%" }}
                      />
                    </div>
                  )}
                </div>
              </div>
            );
          })}
        </div>
      )}

      {isApproved && isOwner && (
        <div style={{ display: "flex", gap: "0.5rem", marginBottom: "0.75rem", alignItems: "center" }}>
          <input
            placeholder="Input source (URL, text, or paste data)"
            value={inputSource}
            onChange={(e) => setInputSource(e.target.value)}
            style={{ flex: 1 }}
          />
          <button className="btn btn-primary" onClick={handleRun} disabled={loading}>
            {loading ? "Running…" : "▶ Run"}
          </button>
        </div>
      )}

      {liveRun && <LiveRunBox state={liveRun} title="Live Orchestration Stream" />}

      <div style={{ display: "flex", gap: "0.5rem", flexWrap: "wrap" }}>
        {isDraft && isOwner && !isEditing && (
          <>
            <button className="btn btn-primary btn-sm" onClick={handleApprove} disabled={loading}>
              ✓ Approve
            </button>
            <button className="btn btn-outline btn-sm" onClick={startEditing}>
              {orch.workflowKind === "Graph" ? "🕸 Edit Graph" : "✏️ Edit List"}
            </button>
            {orch.workflowKind === "Structured" && (
              <button className="btn btn-outline btn-sm" onClick={startGraphEditing}>
                🕸 Graph Editor
              </button>
            )}
            <button className="btn btn-outline btn-sm" onClick={handleReject} disabled={loading}>
              ✕ Reject
            </button>
          </>
        )}

        {isEditing && (
          <>
            <button className="btn btn-primary btn-sm" onClick={saveEdits} disabled={loading}>
              💾 Save Changes
            </button>
            {isStructuredEditing && (
              <button className="btn btn-outline btn-sm" onClick={addStep} disabled={agents.length === 0}>
                + Add Step
              </button>
            )}
            {isStructuredEditing && (
              <button className="btn btn-outline btn-sm" onClick={startGraphEditing} disabled={agents.length === 0}>
                🕸 Switch to Graph
              </button>
            )}
            <button className="btn btn-outline btn-sm" onClick={cancelEditing}>
              Cancel
            </button>
          </>
        )}

        {(orch.status === "Approved" || orch.status === "Rejected" ||
          orch.status === "Completed" || orch.status === "Failed") && isOwner && (
          <button className="btn btn-outline btn-sm" onClick={handleReset} disabled={loading}>
            ↩ Reset to Draft
          </button>
        )}

        {isApproved && isOwner && orch.scope === "Private" && (
          <button className="btn btn-sm btn-primary" onClick={handlePublish} disabled={loading}>
            Publish
          </button>
        )}

        {orch.scope === "Public" && orch.publishedByUserId === currentUserId && (
          <button className="btn btn-sm btn-outline" onClick={handleUnpublish} disabled={loading}>
            Unpublish
          </button>
        )}

        <button className="btn btn-outline btn-sm" onClick={handleClone} disabled={loading}>
          Clone
        </button>

        {isOwner && orch.status !== "Running" && (
          <button
            className="btn btn-sm btn-outline btn-outline-danger"
            onClick={handleDelete}
            disabled={loading}
          >
            Delete
          </button>
        )}
      </div>

      {msg && (
        <div style={{
          marginTop: "0.5rem",
          fontSize: "0.8rem",
          color: msg.includes("Failed") || msg.includes("error") ? "var(--danger)" : "var(--text-muted)",
        }}>
          {msg}
        </div>
      )}
    </div>
  );
}