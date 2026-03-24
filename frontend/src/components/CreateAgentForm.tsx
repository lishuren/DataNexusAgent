import { useEffect, useState } from "react";
import { createAgent, updateAgent } from "@/services/api";
import { useSkills } from "@/hooks/useSkills";
import type { Agent } from "@/types/api";

interface CreateAgentFormProps {
  onCreated: () => void;
  agent?: Agent | null;
  onCancel?: () => void;
}

const KNOWN_PLUGINS = ["InputProcessor", "OutputIntegrator"];

const normalizeList = (value: string[] | string | null | undefined) => {
  if (!value) return [];
  if (Array.isArray(value)) return value;
  return value.split(",").map((item) => item.trim()).filter(Boolean);
};

export function CreateAgentForm({ onCreated, agent, onCancel }: CreateAgentFormProps) {
  const { skills: availableSkills } = useSkills();
  const [execType, setExecType] = useState<"Llm" | "External">("Llm");
  const [name, setName] = useState("");
  const [icon, setIcon] = useState("📧");
  const [description, setDescription] = useState("");
  const [systemPrompt, setSystemPrompt] = useState("");
  const [uiSchema, setUiSchema] = useState<string | undefined>(undefined);
  const [command, setCommand] = useState("");
  const [args, setArgs] = useState("");
  const [workDir, setWorkDir] = useState("");
  const [timeout, setTimeout] = useState(30);
  const [plugins, setPlugins] = useState<string[]>([]);
  const [skills, setSkills] = useState<string[]>([]);
  const [status, setStatus] = useState("");

  useEffect(() => {
    if (agent) {
      setExecType(agent.executionType);
      setName(agent.name);
      setIcon(agent.icon);
      setDescription(agent.description ?? "");
      setSystemPrompt(agent.systemPrompt ?? "");
      setCommand(agent.command ?? "");
      setArgs(agent.arguments ?? "");
      setWorkDir(agent.workingDirectory ?? "");
      setTimeout(agent.timeoutSeconds ?? 30);
      setPlugins(normalizeList(agent.plugins));
      setSkills(normalizeList(agent.skills));
      if (typeof agent.uiSchema === "string") {
        setUiSchema(agent.uiSchema);
      } else if (Array.isArray(agent.uiSchema)) {
        setUiSchema(JSON.stringify(agent.uiSchema));
      } else {
        setUiSchema(undefined);
      }
      setStatus("");
      return;
    }

    setExecType("Llm");
    setName("");
    setIcon("📧");
    setDescription("");
    setSystemPrompt("");
    setCommand("");
    setArgs("");
    setWorkDir("");
    setTimeout(30);
    setPlugins([]);
    setSkills([]);
    setUiSchema(undefined);
    setStatus("");
  }, [agent]);

  const handleSubmit = async () => {
    if (!name.trim()) { setStatus("Name is required"); return; }
    try {
      if (agent) {
        await updateAgent(agent.id, {
          name: name.trim(),
          icon,
          description,
          executionType: execType,
          systemPrompt: execType === "Llm" ? systemPrompt : undefined,
          uiSchema,
          command: execType === "External" ? command : undefined,
          arguments: execType === "External" ? args : undefined,
          workingDirectory: execType === "External" ? workDir || undefined : undefined,
          timeoutSeconds: execType === "External" ? timeout : undefined,
          plugins: plugins.join(","),
          skills: skills.join(","),
        });
        setStatus(`Agent "${name}" updated.`);
        onCreated();
        return;
      }

      await createAgent({
        name: name.trim(),
        icon,
        description,
        executionType: execType,
        systemPrompt: execType === "Llm" ? systemPrompt : undefined,
        uiSchema,
        command: execType === "External" ? command : undefined,
        arguments: execType === "External" ? args : undefined,
        workingDirectory: execType === "External" ? workDir || undefined : undefined,
        timeoutSeconds: execType === "External" ? timeout : undefined,
        plugins: plugins.join(","),
        skills: skills.join(","),
      });
      setStatus(`Agent "${name}" created!`);
      setName(""); setDescription(""); setSystemPrompt(""); setCommand(""); setArgs("");
      onCreated();
    } catch (e) {
      setStatus(e instanceof Error ? e.message : String(e));
    }
  };

  const addPlugin = (p: string) => {
    if (!plugins.includes(p)) setPlugins([...plugins, p]);
  };

  const removePlugin = (p: string) => setPlugins(plugins.filter((x) => x !== p));

  const togglePlugin = (p: string) => {
    plugins.includes(p) ? removePlugin(p) : addPlugin(p);
  };

  const addSkill = (name: string) => {
    if (name && !skills.includes(name)) setSkills([...skills, name]);
  };

  const removeSkill = (name: string) => setSkills(skills.filter((x) => x !== name));

  return (
    <div className="card">
      <h2>{agent ? "✏️ Edit Agent" : "➕ Create Agent"}</h2>

      <div style={{ marginBottom: "0.75rem" }}>
        <label className="form-label">Execution Type</label>
        <div style={{ display: "flex", gap: "0.5rem" }}>
          <button
            className={`btn btn-sm ${execType === "Llm" ? "btn-primary" : "btn-outline"}`}
            type="button"
            onClick={() => setExecType("Llm")}
          >
            🤖 LLM Agent
          </button>
          <button
            className={`btn btn-sm ${execType === "External" ? "btn-primary" : "btn-outline"}`}
            type="button"
            onClick={() => setExecType("External")}
          >
            ⌨️ External (CLI / Script)
          </button>
        </div>
      </div>

      {/* Name + Icon row */}
      <div style={{ display: "flex", gap: "0.75rem", marginBottom: "0.75rem" }}>
        <div style={{ flex: 1 }}>
          <label className="form-label">Name <span style={{ color: "var(--danger)" }}>*</span></label>
          <input
            style={{ marginBottom: 0 }}
            placeholder="Agent name"
            value={name}
            onChange={(e) => setName(e.target.value)}
          />
        </div>
        <div style={{ width: 80 }}>
          <label className="form-label">Icon</label>
          <input
            style={{ marginBottom: 0 }}
            placeholder="📧"
            value={icon}
            onChange={(e) => setIcon(e.target.value)}
          />
        </div>
      </div>

      {/* Description — before prompt so users set intent first */}
      <div style={{ marginBottom: "0.75rem" }}>
        <label className="form-label">Description</label>
        <input
          style={{ marginBottom: 0 }}
          placeholder="Short description shown on agent cards"
          value={description}
          onChange={(e) => setDescription(e.target.value)}
        />
      </div>

      {/* System prompt (LLM) or external execution config */}
      {execType === "Llm" && (
        <div style={{ marginBottom: "0.75rem" }}>
          <label className="form-label">System Prompt</label>
          <textarea
            style={{ marginBottom: 0 }}
            placeholder="Define the agent's role and behavior..."
            value={systemPrompt}
            onChange={(e) => setSystemPrompt(e.target.value)}
          />
        </div>
      )}

      {execType === "External" && (
        <>
          <div style={{ display: "grid", gridTemplateColumns: "1fr 1fr", gap: "0.75rem", marginBottom: "0.75rem" }}>
            <div>
              <label className="form-label">Command <span style={{ color: "var(--danger)" }}>*</span></label>
              <input placeholder="python3, node, bash" value={command} onChange={(e) => setCommand(e.target.value)} style={{ marginBottom: 0 }} />
            </div>
            <div>
              <label className="form-label">Arguments</label>
              <input placeholder="scripts/etl.py --mode transform" value={args} onChange={(e) => setArgs(e.target.value)} style={{ marginBottom: 0 }} />
            </div>
            <div>
              <label className="form-label">Working Directory</label>
              <input placeholder="/tmp/datanexus" value={workDir} onChange={(e) => setWorkDir(e.target.value)} style={{ marginBottom: 0 }} />
            </div>
            <div>
              <label className="form-label">Timeout (seconds)</label>
              <input type="number" value={timeout} min={1} max={120} onChange={(e) => setTimeout(Number(e.target.value))} style={{ marginBottom: 0 }} />
            </div>
          </div>
          <div className="protocol-info" style={{ marginBottom: "0.75rem" }}>
            <div style={{ fontWeight: 600, marginBottom: "0.25rem" }}>📋 stdin/stdout JSON Protocol</div>
            <div style={{ fontSize: "0.75rem", color: "var(--text-muted)", lineHeight: 1.5 }}>
              Your script receives <code>{`{"input":"...","parameters":{...},"userId":"..."}`}</code> on <strong>stdin</strong>.<br />
              It must write <code>{`{"success":true,"message":"...","data":...}`}</code> to <strong>stdout</strong>.<br />
              Exit code 0 = success, non-zero = failure.
            </div>
          </div>
        </>
      )}

      {/* Plugins */}
      <div style={{ marginBottom: "0.75rem" }}>
        <label className="form-label">Plugins</label>
        <div style={{ display: "flex", gap: "0.75rem", flexWrap: "wrap" }}>
          {KNOWN_PLUGINS.map((p) => (
            <label key={p} style={{ display: "flex", alignItems: "center", gap: "0.4rem", cursor: "pointer", fontSize: "0.875rem" }}>
              <input
                type="checkbox"
                checked={plugins.includes(p)}
                onChange={() => togglePlugin(p)}
                style={{ width: "auto", marginBottom: 0 }}
              />
              {p}
            </label>
          ))}
        </div>
      </div>

      {/* Skills */}
      <div style={{ marginBottom: "0.75rem" }}>
        <label className="form-label">Skills <span style={{ fontWeight: 400, color: "var(--text-muted)" }}>(injected into system prompt)</span></label>
        <div style={{ display: "flex", gap: "0.5rem", flexWrap: "wrap", marginBottom: "0.4rem" }}>
          {skills.map((s) => (
            <span key={s} className="badge badge-public" style={{ padding: "4px 10px", cursor: "pointer" }} onClick={() => removeSkill(s)}>
              {s} ✕
            </span>
          ))}
        </div>
        <select
          style={{ marginBottom: 0 }}
          value=""
          onChange={(e) => { addSkill(e.target.value); e.target.value = ""; }}
        >
          <option value="" disabled>— select a skill to add —</option>
          {availableSkills
            .filter((s) => !skills.includes(s.name))
            .map((s) => (
              <option key={s.id} value={s.name}>{s.name}{s.scope === "Public" ? " (public)" : ""}</option>
            ))}
        </select>
      </div>

      {/* UI Schema */}
      <div style={{ marginBottom: "0.75rem" }}>
        <label className="form-label">UI Schema <span style={{ fontWeight: 400, color: "var(--text-muted)" }}>(JSON array — defines form fields on the Process page)</span></label>
        <textarea
          placeholder={'[{"type":"file","name":"inputFile","label":"Upload file","accept":".xlsx"}]'}
          value={uiSchema ?? ""}
          onChange={(e) => setUiSchema(e.target.value || undefined)}
          style={{ minHeight: "80px", fontFamily: "monospace", fontSize: "0.8rem", marginBottom: 0 }}
        />
      </div>

      <div style={{ display: "flex", gap: "0.75rem", flexWrap: "wrap" }}>
        <button className="btn btn-primary" type="button" onClick={handleSubmit}>
          {agent ? "Save Changes" : "Create Agent"}
        </button>
        {agent && onCancel && (
          <button className="btn btn-outline btn-sm" type="button" onClick={onCancel}>
            Cancel
          </button>
        )}
      </div>

      {status && (
        <div className="result-box result-success" style={{ marginTop: "0.75rem" }}>{status}</div>
      )}
    </div>
  );
}
