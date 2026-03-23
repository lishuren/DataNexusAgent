import { useCallback, useEffect, useState } from "react";
import type { Agent, Pipeline, ProcessingResult, UiField } from "@/types/api";
import { listAgents, listPipelines, processData, runPipeline, getAgent } from "@/services/api";
import { AgentSelector } from "@/components/AgentSelector";
import { DynamicForm } from "@/components/DynamicForm";
import { QuickActions } from "@/components/QuickActions";
import { ResultBox } from "@/components/ResultBox";
import { RecentTasks } from "@/components/RecentTasks";

export default function ProcessPage() {
  const [agents, setAgents] = useState<Agent[]>([]);
  const [pipelines, setPipelines] = useState<Pipeline[]>([]);
  const [selectedAgentId, setSelectedAgentId] = useState<number | undefined>();
  const [selectedPipelineId, setSelectedPipelineId] = useState<number | undefined>();
  const [selectedAgent, setSelectedAgent] = useState<Agent | null>(null);
  const [fields, setFields] = useState<UiField[]>([]);
  const [values, setValues] = useState<Record<string, string>>({});
  const [result, setResult] = useState<ProcessingResult | null>(null);
  const [loading, setLoading] = useState(false);

  const normalizeUiSchema = (schema: Agent["uiSchema"]): UiField[] => {
    if (!schema) return [];
    if (Array.isArray(schema)) return schema;
    if (typeof schema === "string") {
      try {
        const parsed = JSON.parse(schema) as UiField[];
        return Array.isArray(parsed) ? parsed : [];
      } catch {
        return [];
      }
    }
    return [];
  };

  const refresh = useCallback(async () => {
    try {
      const [a, p] = await Promise.all([listAgents(), listPipelines()]);
      setAgents(a);
      setPipelines(p);
      if (a.length > 0 && !selectedAgentId && !selectedPipelineId) {
        selectAgent(a[0]!.id, a);
      }
    } catch (e) {
      console.warn("Failed to load agents/pipelines:", e);
    }
  }, []); // eslint-disable-line react-hooks/exhaustive-deps

  useEffect(() => { refresh(); }, [refresh]);

  const selectAgent = async (id: number, agentsList?: Agent[]) => {
    setSelectedAgentId(id);
    setSelectedPipelineId(undefined);
    setResult(null);
    setValues({});
    const list = agentsList ?? agents;
    const cached = list.find((a) => a.id === id);
    if (cached) {
      setSelectedAgent(cached);
      setFields(normalizeUiSchema(cached.uiSchema));
    } else {
      try {
        const a = await getAgent(id);
        setSelectedAgent(a);
        setFields(normalizeUiSchema(a.uiSchema));
      } catch (e) { console.warn("Failed to fetch agent:", e); }
    }
  };

  const selectPipeline = (id: number) => {
    setSelectedPipelineId(id);
    setSelectedAgentId(undefined);
    setSelectedAgent(null);
    setResult(null);
    setValues({});
    // Pipeline uses simple input fields
    setFields([
      { key: "inputSource", label: "Input Source", type: "text", placeholder: "URL or file path", required: true },
      { key: "outputDestination", label: "Output Destination", type: "text", placeholder: "e.g. public-api" },
      { key: "task", label: "Task Description", type: "textarea", placeholder: "Describe the workflow..." },
    ]);
  };

  const handleRun = async () => {
    setLoading(true);
    setResult(null);
    try {
      let res: ProcessingResult;
      if (selectedPipelineId) {
        const pipe = pipelines.find((p) => p.id === selectedPipelineId);
        if (!pipe) throw new Error("Pipeline not found");
        res = await runPipeline({
          name: pipe.name,
          agentIds: pipe.agentIds,
          inputSource: values["inputSource"] ?? "",
          outputDestination: values["outputDestination"] ?? "",
          enableSelfCorrection: pipe.enableSelfCorrection,
          maxCorrectionAttempts: pipe.maxCorrectionAttempts,
          parameters: values,
        });
      } else {
        res = await processData({
          agentId: selectedAgentId,
          inputSource: values["file"] ?? values["data"] ?? values["inputSource"] ?? "",
          outputDestination: values["endpoint"] ?? values["outputFormat"] ?? "json",
          skillName: values["skill"],
          parameters: values,
        });
      }
      setResult(res);
    } catch (e) {
      setResult({ success: false, message: e instanceof Error ? e.message : String(e) });
    } finally {
      setLoading(false);
    }
  };

  const activePipeline = selectedPipelineId ? pipelines.find((p) => p.id === selectedPipelineId) : null;
  const title = activePipeline
    ? `🔗 ${activePipeline.name}`
    : selectedAgent
      ? `${selectedAgent.icon} ${selectedAgent.name}`
      : "Select an agent";

  const hint = activePipeline
    ? `Running: ${activePipeline.name} (${activePipeline.agentIds.length} steps${activePipeline.enableSelfCorrection ? ", self-correction on" : ""})`
    : selectedAgent
      ? `Using: ${selectedAgent.name}${selectedAgent.executionType === "External" ? " (CLI/Script)" : ""}`
      : "";

  return (
    <>
      <AgentSelector
        agents={agents}
        pipelines={pipelines}
        selectedAgentId={selectedAgentId}
        selectedPipelineId={selectedPipelineId}
        onSelectAgent={(id) => selectAgent(id)}
        onSelectPipeline={selectPipeline}
      />

      <div className="card">
        <h2>{title}</h2>

        {activePipeline && (
          <div style={{ padding: "0.75rem", background: "rgba(var(--primary-rgb, 99,102,241),0.05)", border: "1px solid var(--border)", borderRadius: "0.5rem", marginBottom: "1rem" }}>
            <div style={{ fontSize: "0.75rem", color: "var(--text-muted)", marginBottom: "0.25rem" }}>Pipeline steps:</div>
            <div style={{ fontSize: "0.85rem", display: "flex", alignItems: "center", gap: "0.4rem", flexWrap: "wrap" }}>
              {activePipeline.agentIds.map((aid, i) => {
                const a = agents.find((x) => x.id === aid);
                return (
                  <span key={i}>
                    {i > 0 && <span style={{ color: "var(--text-muted)", margin: "0 0.2rem" }}>→</span>}
                    {a ? `${a.icon} ${a.name}` : `Agent #${aid}`}
                  </span>
                );
              })}
            </div>
          </div>
        )}

        <DynamicForm fields={fields} values={values} onChange={(key, val) => setValues((prev) => ({ ...prev, [key]: val }))} />

        <div style={{ display: "flex", gap: "0.75rem", alignItems: "center", marginTop: "0.75rem" }}>
          <button className="btn btn-primary" onClick={handleRun} disabled={loading}>
            {loading ? "Running…" : activePipeline ? "▶ Run Pipeline" : "▶ Run Task"}
          </button>
          <span style={{ color: "var(--text-muted)", fontSize: "0.8rem" }}>{hint}</span>
        </div>
      </div>

      <QuickActions actions={[]} />

      {result && <ResultBox result={result} />}

      <RecentTasks />
    </>
  );
}
