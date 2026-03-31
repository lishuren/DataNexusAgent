import { useCallback, useEffect, useState } from "react";
import type { Agent, ConcurrentAggregatorMode, ExecutionMode, Pipeline } from "@/types/api";
import {
  listAgents,
  listPipelines,
  getAgent,
  publishAgent,
  unpublishAgent,
  deleteAgent,
  cloneAgent,
  createPipeline,
  updatePipeline,
  deletePipeline as apiDeletePipeline,
  clonePipeline,
  publishPipeline,
  unpublishPipeline,
} from "@/services/api";
import { getUserId } from "@/services/auth";
import { AgentCard } from "@/components/AgentCard";
import { CreateAgentForm } from "@/components/CreateAgentForm";
import { PipelineBuilder } from "@/components/PipelineBuilder";
import { SavedPipelines } from "@/components/SavedPipelines";

const PIPELINE_MODE_OPTIONS: Array<{ value: ExecutionMode; label: string }> = [
  { value: "Sequential", label: "Sequential" },
  { value: "Concurrent", label: "Concurrent" },
  { value: "Handoff", label: "Handoff" },
  { value: "GroupChat", label: "Group Chat" },
];

const PIPELINE_MODE_HELP: Record<ExecutionMode, string> = {
  Sequential: "Each step runs in order, passing its output to the next agent.",
  Concurrent: "All steps run in parallel and their outputs are merged with the selected aggregator.",
  Handoff: "The first pipeline step acts as the triage agent and routes work to the remaining steps.",
  GroupChat: "All steps join a round-robin discussion. Saved pipelines currently use the default 10-turn cap.",
};

const CONCURRENT_AGGREGATOR_OPTIONS: ConcurrentAggregatorMode[] = ["Concatenate", "First", "Last"];

export default function AgentsPage() {
  const pluginCatalog = [
    {
      name: "InputProcessor",
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
  const [agentMsg, setAgentMsg] = useState("");
  const [pipelineName, setPipelineName] = useState("");
  const [pipelineSteps, setPipelineSteps] = useState<number[]>([]);
  const [selfCorrection, setSelfCorrection] = useState(true);
  const [pipelineExecutionMode, setPipelineExecutionMode] = useState<ExecutionMode>("Sequential");
  const [pipelineConcurrentAggregatorMode, setPipelineConcurrentAggregatorMode] = useState<ConcurrentAggregatorMode>("Concatenate");
  const [editingPipelineId, setEditingPipelineId] = useState<number | null>(null);
  const [pipelineMsg, setPipelineMsg] = useState("");
  const userId = getUserId();

  const refresh = useCallback(async () => {
    try {
      const [a, p] = await Promise.all([listAgents(), listPipelines()]);
      setAgents(a);
      setPipelines(p);
    } catch (e) {
      console.warn("Failed to load agents/pipelines:", e);
    }
  }, []);

  useEffect(() => { refresh(); }, [refresh]);

  const handlePublish = async (id: number) => {
    try {
      await publishAgent(id);
      refresh();
    } catch (e) {
      setAgentMsg(`Failed to publish: ${e instanceof Error ? e.message : String(e)}`);
    }
  };

  const handleUnpublish = async (id: number) => {
    try {
      await unpublishAgent(id);
      refresh();
    } catch (e) {
      setAgentMsg(`Failed to unpublish: ${e instanceof Error ? e.message : String(e)}`);
    }
  };

  const handleEditAgent = async (agent: Agent) => {
    try {
      const full = await getAgent(agent.id);
      setEditingAgent(full);
    } catch {
      setEditingAgent(agent); // fallback to list data
    }
  };

  const handleDeleteAgent = async (id: number) => {
    try {
      await deleteAgent(id);
      setEditingAgent((current) => (current?.id === id ? null : current));
      refresh();
    } catch (e) {
      setAgentMsg(`Failed to delete: ${e instanceof Error ? e.message : String(e)}`);
    }
  };

  const handleCloneAgent = async (agent: Agent) => {
    const name = window.prompt("Clone agent name", `${agent.name}`);
    if (!name?.trim()) return;
    try {
      await cloneAgent(agent.id, name.trim());
      refresh();
    } catch (e) {
      setAgentMsg(`Failed to clone: ${e instanceof Error ? e.message : String(e)}`);
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
          executionMode: pipelineExecutionMode,
          concurrentAggregatorMode: pipelineConcurrentAggregatorMode,
        });
      } else {
        await createPipeline({
          name: pipelineName,
          agentIds: pipelineSteps,
          enableSelfCorrection: selfCorrection,
          executionMode: pipelineExecutionMode,
          concurrentAggregatorMode: pipelineConcurrentAggregatorMode,
        });
      }
      setPipelineMsg(`Pipeline "${pipelineName}" saved in ${pipelineExecutionMode} mode with ${pipelineSteps.length} steps.`);
      setPipelineName("");
      setPipelineSteps([]);
      setSelfCorrection(true);
      setPipelineExecutionMode("Sequential");
      setPipelineConcurrentAggregatorMode("Concatenate");
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
    setPipelineExecutionMode(p.executionMode);
    setPipelineConcurrentAggregatorMode(p.concurrentAggregatorMode);
  };

  const handleDeletePipeline = async (id: number) => {
    try {
      await apiDeletePipeline(id);
      refresh();
    } catch (e) {
      setPipelineMsg(`Failed to delete: ${e instanceof Error ? e.message : String(e)}`);
    }
  };

  const handleClonePipeline = async (pipeline: Pipeline) => {
    const name = window.prompt("Clone pipeline name", pipeline.name);
    if (!name?.trim()) return;
    try {
      await clonePipeline(pipeline.id, name.trim());
      refresh();
    } catch (e) {
      setPipelineMsg(`Failed to clone: ${e instanceof Error ? e.message : String(e)}`);
    }
  };

  const handlePublishPipeline = async (id: number) => {
    try {
      await publishPipeline(id);
      refresh();
    } catch (e) {
      setPipelineMsg(`Failed to publish: ${e instanceof Error ? e.message : String(e)}`);
    }
  };

  const handleUnpublishPipeline = async (id: number) => {
    try {
      await unpublishPipeline(id);
      refresh();
    } catch (e) {
      setPipelineMsg(`Failed to unpublish: ${e instanceof Error ? e.message : String(e)}`);
    }
  };

  const clearPipeline = () => {
    setPipelineSteps([]);
    setPipelineName("");
    setSelfCorrection(true);
    setPipelineExecutionMode("Sequential");
    setPipelineConcurrentAggregatorMode("Concatenate");
    setEditingPipelineId(null);
    setPipelineMsg("");
  };

  return (
    <>
      {/* My Agents */}
      <div className="card">
        <h2>🤖 My Agents</h2>
        {agentMsg && (
          <p style={{ color: "var(--danger)", fontSize: "0.875rem", marginBottom: "0.75rem" }}>{agentMsg}</p>
        )}
        <div className="agent-grid">
          {agents.map((a) => (
            <div key={a.id} className="agent-card-wrapper">
              {a.scope === "Private" && !a.isBuiltIn && a.ownerId === userId && (
                <div className="agent-card-publish-corner">
                  <button
                    className="btn btn-sm btn-primary"
                    onClick={(e) => { e.stopPropagation(); handlePublish(a.id); }}
                  >
                    Publish
                  </button>
                </div>
              )}
              {a.scope === "Public" && a.publishedByUserId === userId && (
                <div className="agent-card-publish-corner">
                  <button
                    className="btn btn-sm btn-outline"
                    onClick={(e) => { e.stopPropagation(); handleUnpublish(a.id); }}
                  >
                    Unpublish
                  </button>
                </div>
              )}
              <AgentCard agent={a} />
              <div className="agent-card-actions">
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
        <div style={{ display: "grid", gridTemplateColumns: "repeat(auto-fit, minmax(220px, 1fr))", gap: "0.75rem", marginBottom: "0.5rem" }}>
          <div>
            <label style={{ fontSize: "0.8rem", color: "var(--text-muted)", display: "block", marginBottom: 4 }}>
              Execution Mode
            </label>
            <select
              value={pipelineExecutionMode}
              onChange={(e) => setPipelineExecutionMode(e.target.value as ExecutionMode)}
              style={{ marginBottom: 0 }}
            >
              {PIPELINE_MODE_OPTIONS.map((option) => (
                <option key={option.value} value={option.value}>{option.label}</option>
              ))}
            </select>
          </div>

          {pipelineExecutionMode === "Concurrent" && (
            <div>
              <label style={{ fontSize: "0.8rem", color: "var(--text-muted)", display: "block", marginBottom: 4 }}>
                Concurrent Aggregator
              </label>
              <select
                value={pipelineConcurrentAggregatorMode}
                onChange={(e) => setPipelineConcurrentAggregatorMode(e.target.value as ConcurrentAggregatorMode)}
                style={{ marginBottom: 0 }}
              >
                {CONCURRENT_AGGREGATOR_OPTIONS.map((option) => (
                  <option key={option} value={option}>{option}</option>
                ))}
              </select>
            </div>
          )}
        </div>
        <div style={{ fontSize: "0.75rem", color: "var(--text-muted)", marginBottom: "0.75rem" }}>
          {PIPELINE_MODE_HELP[pipelineExecutionMode]}
        </div>
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
          onPublish={handlePublishPipeline}
          onUnpublish={handleUnpublishPipeline}
          currentUserId={userId}
        />
      </div>
    </>
  );
}
