import { useCallback, useEffect, useState } from "react";
import type { Agent, Orchestration, ProcessingResult } from "@/types/api";
import {
  listAgents,
  listOrchestrations,
  planOrchestration,
} from "@/services/api";
import { getUserId } from "@/services/auth";
import { OrchestrationReview } from "@/components/OrchestrationReview";
import { ResultBox } from "@/components/ResultBox";

export default function OrchestrationsPage() {
  const [agents, setAgents] = useState<Agent[]>([]);
  const [orchestrations, setOrchestrations] = useState<Orchestration[]>([]);
  const [result, setResult] = useState<ProcessingResult | null>(null);

  // Planner form state
  const [planGoal, setPlanGoal] = useState("");
  const [planConstraints, setPlanConstraints] = useState("");
  const [planning, setPlanning] = useState(false);
  const [planMsg, setPlanMsg] = useState("");

  // Filter
  const [statusFilter, setStatusFilter] = useState<string>("all");

  const userId = getUserId();

  const refresh = useCallback(async () => {
    try {
      const [a, o] = await Promise.all([listAgents(), listOrchestrations()]);
      setAgents(a);
      setOrchestrations(o);
    } catch (e) {
      console.warn("Failed to load orchestrations:", e);
    }
  }, []);

  useEffect(() => { refresh(); }, [refresh]);

  const handlePlan = async () => {
    if (!planGoal.trim()) return;
    setPlanning(true);
    setPlanMsg("");
    try {
      await planOrchestration({
        goal: planGoal.trim(),
        constraints: planConstraints.trim() || undefined,
      });
      setPlanGoal("");
      setPlanConstraints("");
      setPlanMsg("Plan generated! Review it below.");
      refresh();
    } catch (e) {
      setPlanMsg(e instanceof Error ? e.message : String(e));
    }
    setPlanning(false);
  };

  const filtered = statusFilter === "all"
    ? orchestrations
    : orchestrations.filter((o) => o.status === statusFilter);

  const statusCounts = orchestrations.reduce<Record<string, number>>((acc, o) => {
    acc[o.status] = (acc[o.status] ?? 0) + 1;
    return acc;
  }, {});

  return (
    <>
      {/* ── AI Orchestration Planner ──────────────────────────── */}
      <div className="card">
        <h2>🧠 AI Orchestration Planner</h2>
        <p style={{ fontSize: "0.8rem", color: "var(--text-muted)", marginBottom: "0.75rem" }}>
          Describe a goal and the AI will decompose it into an ordered agent workflow.
          Review, edit agents &amp; prompts, approve, then run.
        </p>
        <textarea
          placeholder={"Describe your goal, e.g.\n• Parse my sales Excel, validate data quality, push clean records to the CRM API\n• Extract invoice line items, convert currencies, generate a summary report"}
          value={planGoal}
          onChange={(e) => setPlanGoal(e.target.value)}
          style={{ minHeight: "80px", marginBottom: "0.5rem" }}
        />
        <input
          placeholder="Constraints (optional), e.g. 'Use JSON as intermediate format, skip rows with missing emails'"
          value={planConstraints}
          onChange={(e) => setPlanConstraints(e.target.value)}
          style={{ marginBottom: "0.5rem" }}
        />
        <div style={{ display: "flex", gap: "0.75rem", alignItems: "center" }}>
          <button
            className="btn btn-primary"
            disabled={planning || !planGoal.trim()}
            onClick={handlePlan}
          >
            {planning ? "Planning…" : "🧠 Generate Plan"}
          </button>
          <span style={{ fontSize: "0.8rem", color: "var(--text-muted)" }}>
            Uses GPT-4o to decompose your goal into agent steps
          </span>
        </div>
        {planMsg && (
          <div style={{
            marginTop: "0.5rem",
            fontSize: "0.8rem",
            color: planMsg.includes("generated") ? "var(--success)" : "var(--danger)",
          }}>
            {planMsg}
          </div>
        )}
      </div>

      {/* ── Execution result ─────────────────────────────────── */}
      {result && <ResultBox result={result} />}

      {/* ── My Orchestrations ────────────────────────────────── */}
      <div className="card">
        <div style={{ display: "flex", justifyContent: "space-between", alignItems: "center", marginBottom: "0.75rem" }}>
          <h2 style={{ margin: 0 }}>📋 My Orchestrations ({orchestrations.length})</h2>

          {/* Status filter pills */}
          <div style={{ display: "flex", gap: "0.35rem", flexWrap: "wrap" }}>
            {["all", "Draft", "Approved", "Running", "Completed", "Failed", "Rejected"].map((s) => {
              const count = s === "all" ? orchestrations.length : (statusCounts[s] ?? 0);
              if (s !== "all" && count === 0) return null;
              return (
                <button
                  key={s}
                  className={`btn btn-sm ${statusFilter === s ? "btn-primary" : "btn-outline"}`}
                  onClick={() => setStatusFilter(s)}
                  style={{ fontSize: "0.7rem", padding: "2px 8px" }}
                >
                  {s === "all" ? "All" : s} ({count})
                </button>
              );
            })}
          </div>
        </div>

        {filtered.length === 0 ? (
          <p style={{ color: "var(--text-muted)", fontSize: "0.875rem" }}>
            {orchestrations.length === 0
              ? "No orchestrations yet. Use the planner above to create one."
              : "No orchestrations match this filter."}
          </p>
        ) : (
          <div style={{ display: "flex", flexDirection: "column", gap: "0.75rem" }}>
            {filtered.map((o) => (
              <OrchestrationReview
                key={o.id}
                orchestration={o}
                agents={agents}
                currentUserId={userId}
                onUpdated={refresh}
                onResult={setResult}
              />
            ))}
          </div>
        )}
      </div>
    </>
  );
}
