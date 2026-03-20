import { useCallback, useEffect, useState } from "react";
import type { Agent, Pipeline } from "@/types/api";
import {
  listAgents,
  listPipelines,
  publishAgent,
  unpublishAgent,
  deleteAgent,
  cloneAgent,
  createPipeline,
  updatePipeline,
  deletePipeline as apiDeletePipeline,
  clonePipeline,
} from "@/services/api";
import { getUserId } from "@/services/auth";
import { AgentCard } from "@/components/AgentCard";
import { CreateAgentForm } from "@/components/CreateAgentForm";
import { PipelineBuilder } from "@/components/PipelineBuilder";
import { SavedPipelines } from "@/components/SavedPipelines";

export default function AgentsPage() {
  const pluginCatalog = [
    {
      name: "ExcelParser",
      description: "Parses Excel/CSV/JSON input into structured JSON for the agent.",
    },
    {
      name: "OutputIntegrator",
      description: "Validates output schema and executes API/database writes.",
    },
  ];

  const [agents, setAgents] = useState<Agent[]>([]);
  const [pipelines, setPipelines] = useState<Pipeline[]>([]);
  const [editingAgent, setEditingAgent] = useState<Agent | null>(null);
  const [pipelineName, setPipelineName] = useState("");
  const [pipelineSteps, setPipelineSteps] = useState<number[]>([]);
  const [selfCorrection, setSelfCorrection] = useState(true);
  const [editingPipelineId, setEditingPipelineId] = useState<number | null>(null);
  const [pipelineMsg, setPipelineMsg] = useState("");
  const userId = getUserId();

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

  const handleUnpublish = async (id: number) => {
    try {
      await unpublishAgent(id);
      refresh();
    } catch {
      /* ignore */
    }
  };

  const handleEditAgent = (agent: Agent) => {
    setEditingAgent(agent);
  };

  const handleDeleteAgent = async (id: number) => {
    try {
      await deleteAgent(id);
      setEditingAgent((current) => (current?.id === id ? null : current));
      refresh();
    } catch {
      /* ignore */
    }
  };

  const handleCloneAgent = async (agent: Agent) => {
    const name = window.prompt("Clone agent name", `${agent.name}`);
    if (!name?.trim()) return;
    try {
      await cloneAgent(agent.id, name.trim());
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

  const handleClonePipeline = async (pipeline: Pipeline) => {
    const name = window.prompt("Clone pipeline name", pipeline.name);
    if (!name?.trim()) return;
    try {
      await clonePipeline(pipeline.id, name.trim());
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
              <div style={{ position: "absolute", top: 8, right: 8, display: "flex", gap: "0.35rem" }}>
                {a.scope === "Private" && !a.isBuiltIn && a.ownerId === userId && (
                  <button
                    className="btn btn-sm btn-primary"
                    onClick={() => handlePublish(a.id)}
                  >
                    Publish
                  </button>
                )}
                {a.scope === "Public" && a.publishedByUserId === userId && (
                  <button
                    className="btn btn-sm btn-outline"
                    onClick={() => handleUnpublish(a.id)}
                  >
                    Unpublish
                  </button>
                )}
                {a.ownerId === userId && a.scope === "Private" && !a.isBuiltIn && (
                  <button
                    className="btn btn-sm btn-outline"
                    onClick={() => handleEditAgent(a)}
                  >
                    Edit
                  </button>
                )}
                {a.ownerId === userId && a.scope === "Private" && !a.isBuiltIn && (
                  <button
                    className="btn btn-sm btn-outline btn-outline-danger"
                    onClick={() => handleDeleteAgent(a.id)}
                  >
                    Delete
                  </button>
                )}
                <button
                  className="btn btn-sm btn-outline"
                  onClick={() => handleCloneAgent(a)}
                >
                  Clone
                </button>
              </div>
            </div>
          ))}
        </div>
      </div>

      {/* Create Agent */}
      <CreateAgentForm
        onCreated={() => { setEditingAgent(null); refresh(); }}
        agent={editingAgent}
        onCancel={() => setEditingAgent(null)}
      />

      {/* Available Plugins */}
      <div className="card">
        <h2>🧩 Available Plugins</h2>
        <div style={{ display: "grid", gap: "0.75rem" }}>
          {pluginCatalog.map((p) => (
            <div key={p.name} style={{ display: "flex", justifyContent: "space-between", gap: "1rem" }}>
              <div>
                <div style={{ fontWeight: 600 }}>{p.name}</div>
                <div style={{ fontSize: "0.8rem", color: "var(--text-muted)" }}>{p.description}</div>
              </div>
              <span className="badge badge-private" style={{ alignSelf: "center" }}>Plugin</span>
            </div>
          ))}
        </div>
        <div style={{ fontSize: "0.75rem", color: "var(--text-muted)", marginTop: "0.75rem" }}>
          Use the plugin name exactly as shown when adding it to an agent.
        </div>
      </div>

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
          onClone={handleClonePipeline}
          currentUserId={userId}
        />
      </div>
    </>
  );
}
