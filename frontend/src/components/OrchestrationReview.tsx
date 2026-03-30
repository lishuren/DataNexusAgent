import { useState } from "react";
import type { Agent, Orchestration, OrchestrationStep } from "@/types/api";
import {
  approveOrchestration,
  rejectOrchestration,
  resetOrchestration,
  updateOrchestration,
  deleteOrchestration,
  runOrchestration,
  publishOrchestration,
  unpublishOrchestration,
  cloneOrchestration,
} from "@/services/api";
import type { ProcessingResult } from "@/types/api";

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

export function OrchestrationReview({
  orchestration: orch,
  agents,
  currentUserId,
  onUpdated,
  onResult,
}: OrchestrationReviewProps) {
  const [editingSteps, setEditingSteps] = useState<OrchestrationStep[] | null>(null);
  const [editingName, setEditingName] = useState(orch.name);
  const [inputSource, setInputSource] = useState("");
  const [loading, setLoading] = useState(false);
  const [msg, setMsg] = useState("");

  const isOwner = orch.ownerId === currentUserId;
  const isDraft = orch.status === "Draft";
  const isApproved = orch.status === "Approved";
  const steps = editingSteps ?? orch.steps;
  const badge = STATUS_BADGE[orch.status] ?? { className: "badge", label: orch.status };

  const getAgent = (id: number) => agents.find((a) => a.id === id);

  // ── Step editing ───────────────────────────────────────────────────

  const startEditing = () => {
    setEditingSteps(orch.steps.map((s) => ({ ...s })));
    setEditingName(orch.name);
  };

  const cancelEditing = () => {
    setEditingSteps(null);
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
      .map((s, i) => ({ ...s, stepNumber: i + 1 }));
    setEditingSteps(next);
  };

  const saveEdits = async () => {
    if (!editingSteps || editingSteps.length < 1) {
      setMsg("At least one step is required.");
      return;
    }
    setLoading(true);
    try {
      await updateOrchestration(orch.id, {
        name: editingName,
        steps: editingSteps,
        enableSelfCorrection: orch.enableSelfCorrection,
        maxCorrectionAttempts: orch.maxCorrectionAttempts,
      });
      setEditingSteps(null);
      setMsg("Changes saved.");
      onUpdated();
    } catch (e) {
      setMsg(e instanceof Error ? e.message : String(e));
    }
    setLoading(false);
  };

  // ── Status actions ─────────────────────────────────────────────────

  const handleApprove = async () => {
    setLoading(true);
    try {
      await approveOrchestration(orch.id);
      setMsg("Orchestration approved.");
      onUpdated();
    } catch (e) { setMsg(e instanceof Error ? e.message : String(e)); }
    setLoading(false);
  };

  const handleReject = async () => {
    const reason = window.prompt("Rejection reason (optional):");
    setLoading(true);
    try {
      await rejectOrchestration(orch.id, reason ?? undefined);
      setMsg("Orchestration rejected.");
      onUpdated();
    } catch (e) { setMsg(e instanceof Error ? e.message : String(e)); }
    setLoading(false);
  };

  const handleReset = async () => {
    setLoading(true);
    try {
      await resetOrchestration(orch.id);
      setMsg("Reset to Draft.");
      onUpdated();
    } catch (e) { setMsg(e instanceof Error ? e.message : String(e)); }
    setLoading(false);
  };

  const handleRun = async () => {
    if (!inputSource.trim()) { setMsg("Input source is required to run."); return; }
    setLoading(true);
    try {
      const result = await runOrchestration(orch.id, inputSource);
      setMsg(result.success ? "Orchestration completed!" : `Failed: ${result.message}`);
      onResult?.(result);
      onUpdated();
    } catch (e) { setMsg(e instanceof Error ? e.message : String(e)); }
    setLoading(false);
  };

  const handleDelete = async () => {
    setLoading(true);
    try {
      await deleteOrchestration(orch.id);
      onUpdated();
    } catch (e) { setMsg(e instanceof Error ? e.message : String(e)); }
    setLoading(false);
  };

  const handlePublish = async () => {
    setLoading(true);
    try {
      await publishOrchestration(orch.id);
      setMsg("Published to marketplace.");
      onUpdated();
    } catch (e) { setMsg(e instanceof Error ? e.message : String(e)); }
    setLoading(false);
  };

  const handleUnpublish = async () => {
    setLoading(true);
    try {
      await unpublishOrchestration(orch.id);
      setMsg("Moved back to private.");
      onUpdated();
    } catch (e) { setMsg(e instanceof Error ? e.message : String(e)); }
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
    } catch (e) { setMsg(e instanceof Error ? e.message : String(e)); }
    setLoading(false);
  };

  return (
    <div className="card" style={{ borderLeft: "3px solid var(--primary)" }}>
      {/* Header */}
      <div style={{ display: "flex", justifyContent: "space-between", alignItems: "center", marginBottom: "0.75rem" }}>
        <div>
          <h3 style={{ margin: 0 }}>
            🗂️ {editingSteps ? (
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
        <span className={badge.className}>{badge.label}</span>
      </div>

      {/* Planner notes */}
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

      {/* Steps */}
      <div style={{ display: "flex", flexDirection: "column", gap: "0.5rem", marginBottom: "0.75rem" }}>
        {steps.map((step, idx) => {
          const agent = getAgent(step.agentId);
          return (
            <div key={idx} style={{
              display: "flex",
              alignItems: "flex-start",
              gap: "0.75rem",
              padding: "0.6rem 0.75rem",
              background: "var(--surface)",
              border: "1px solid var(--border)",
              borderRadius: "0.5rem",
            }}>
              <div style={{
                width: 28, height: 28, borderRadius: "50%",
                background: "var(--primary)", color: "#fff",
                display: "flex", alignItems: "center", justifyContent: "center",
                fontSize: "0.8rem", fontWeight: 700, flexShrink: 0,
              }}>
                {step.stepNumber}
              </div>
              <div style={{ flex: 1 }}>
                <div style={{ fontWeight: 600, fontSize: "0.85rem" }}>
                  {step.title}
                  {step.isEdited && (
                    <span style={{ color: "var(--primary)", fontSize: "0.7rem", marginLeft: "0.4rem" }}>edited</span>
                  )}
                </div>
                <div style={{ fontSize: "0.8rem", color: "var(--text-muted)" }}>{step.description}</div>
                <div style={{ fontSize: "0.75rem", marginTop: "0.25rem" }}>
                  Agent: {agent?.icon ?? "🤖"} <strong>{step.agentName}</strong>
                </div>

                {/* Prompt override when editing */}
                {editingSteps && (
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

      {/* Run controls (approved only) */}
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

      {/* Action buttons */}
      <div style={{ display: "flex", gap: "0.5rem", flexWrap: "wrap" }}>
        {isDraft && isOwner && !editingSteps && (
          <>
            <button className="btn btn-primary btn-sm" onClick={handleApprove} disabled={loading}>
              ✓ Approve
            </button>
            <button className="btn btn-outline btn-sm" onClick={startEditing}>
              ✏️ Edit
            </button>
            <button className="btn btn-outline btn-sm" onClick={handleReject} disabled={loading}>
              ✕ Reject
            </button>
          </>
        )}

        {editingSteps && (
          <>
            <button className="btn btn-primary btn-sm" onClick={saveEdits} disabled={loading}>
              💾 Save Changes
            </button>
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
