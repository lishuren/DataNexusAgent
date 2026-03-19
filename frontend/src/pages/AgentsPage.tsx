import { useCallback, useEffect, useState } from "react";
import type { Agent, Pipeline } from "@/types/api";
import {
  listAgents,
  listPipelines,
  publishAgent,
  createPipeline,
  updatePipeline,
  deletePipeline as apiDeletePipeline,
} from "@/services/api";
import { AgentCard } from "@/components/AgentCard";
import { CreateAgentForm } from "@/components/CreateAgentForm";
import { PipelineBuilder } from "@/components/PipelineBuilder";
import { SavedPipelines } from "@/components/SavedPipelines";

export default function AgentsPage() {
  const [agents, setAgents] = useState<Agent[]>([]);
  const [pipelines, setPipelines] = useState<Pipeline[]>([]);
  const [pipelineName, setPipelineName] = useState("");
  const [pipelineSteps, setPipelineSteps] = useState<number[]>([]);
  const [selfCorrection, setSelfCorrection] = useState(true);
  const [editingPipelineId, setEditingPipelineId] = useState<number | null>(null);
  const [pipelineMsg, setPipelineMsg] = useState("");

  const refresh = useCallback(async () => {
    try {
      const [a, p] = await Promise.all([listAgents(), listPipelines()]);
      setAgents(a);
      setPipelines(p);
    } catch {
      /* ignore */
    }
  }, []);

  useEffect(() => { refresh(); }, [refresh]);

  const handlePublish = async (id: number) => {
    try {
      await publishAgent(id);
      refresh();
    } catch {
      /* ignore */
    }
  };

  const handleSavePipeline = async () => {
    if (!pipelineName.trim()) { setPipelineMsg("Name is required"); return; }
    if (pipelineSteps.length < 2) { setPipelineMsg("A pipeline must have at least 2 steps"); return; }
    try {
      if (editingPipelineId) {
        await updatePipeline(editingPipelineId, {
          name: pipelineName,
          agentIds: pipelineSteps,
          enableSelfCorrection: selfCorrection,
        });
      } else {
        await createPipeline({
          name: pipelineName,
          agentIds: pipelineSteps,
          enableSelfCorrection: selfCorrection,
        });
      }
      setPipelineMsg(`Pipeline "${pipelineName}" saved with ${pipelineSteps.length} steps.`);
      setPipelineName("");
      setPipelineSteps([]);
      setEditingPipelineId(null);
      refresh();
    } catch (e) {
      setPipelineMsg(e instanceof Error ? e.message : String(e));
    }
  };

  const handleEditPipeline = (p: Pipeline) => {
    setEditingPipelineId(p.id);
    setPipelineName(p.name);
    setPipelineSteps(p.agentIds);
    setSelfCorrection(p.enableSelfCorrection);
  };

  const handleDeletePipeline = async (id: number) => {
    try {
      await apiDeletePipeline(id);
      refresh();
    } catch {
      /* ignore */
    }
  };

  const clearPipeline = () => {
    setPipelineSteps([]);
    setPipelineName("");
    setEditingPipelineId(null);
    setPipelineMsg("");
  };

  return (
    <>
      {/* My Agents */}
      <div className="card">
        <h2>🤖 My Agents</h2>
        <div className="agent-grid">
          {agents.map((a) => (
            <div key={a.id} style={{ position: "relative" }}>
              <AgentCard agent={a} />
              {a.scope === "Private" && !a.isBuiltIn && (
                <button
                  className="btn btn-sm btn-primary"
                  style={{ position: "absolute", top: 8, right: 8 }}
                  onClick={() => handlePublish(a.id)}
                >
                  Publish
                </button>
              )}
            </div>
          ))}
        </div>
      </div>

      {/* Create Agent */}
      <CreateAgentForm onCreated={refresh} />

      {/* Pipeline Builder */}
      <div className="card">
        <h2>🔗 Compose Agent Pipeline</h2>
        <p style={{ fontSize: "0.8rem", color: "var(--text-muted)", marginBottom: "0.75rem" }}>
          Chain agents together — output of one feeds into the next. Self-correction loops back on schema errors.
        </p>
        <input
          placeholder="Pipeline name"
          value={pipelineName}
          onChange={(e) => setPipelineName(e.target.value)}
        />
        <PipelineBuilder steps={pipelineSteps} agents={agents} onChange={setPipelineSteps} />
        <div style={{ display: "flex", gap: "0.75rem", alignItems: "center", flexWrap: "wrap" }}>
          <button className="btn btn-primary" onClick={handleSavePipeline}>
            {editingPipelineId ? "Update Pipeline" : "Save Pipeline"}
          </button>
          <button
            className="btn btn-outline btn-sm"
            style={{ color: "var(--danger)", borderColor: "var(--danger)" }}
            onClick={clearPipeline}
          >
            Clear All
          </button>
          <label style={{ fontSize: "0.8rem", color: "var(--text-muted)" }}>
            <input
              type="checkbox"
              checked={selfCorrection}
              onChange={(e) => setSelfCorrection(e.target.checked)}
              style={{ width: "auto", marginRight: 4, marginBottom: 0 }}
            />
            Enable self-correction (retry on schema mismatch, max 3)
          </label>
        </div>
        {pipelineMsg && (
          <div className="result-box result-success" style={{ marginTop: "0.75rem" }}>{pipelineMsg}</div>
        )}
      </div>

      {/* Saved Pipelines */}
      <div className="card">
        <h2>📋 Saved Pipelines</h2>
        <SavedPipelines
          pipelines={pipelines}
          agents={agents}
          onEdit={handleEditPipeline}
          onDelete={handleDeletePipeline}
        />
      </div>
    </>
  );
}
