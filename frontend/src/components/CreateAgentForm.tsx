import { useState } from "react";
import { createAgent } from "@/services/api";

interface CreateAgentFormProps {
  onCreated: () => void;
}

export function CreateAgentForm({ onCreated }: CreateAgentFormProps) {
  const [execType, setExecType] = useState<"Llm" | "External">("Llm");
  const [name, setName] = useState("");
  const [icon, setIcon] = useState("📧");
  const [description, setDescription] = useState("");
  const [systemPrompt, setSystemPrompt] = useState("");
  const [command, setCommand] = useState("");
  const [args, setArgs] = useState("");
  const [workDir, setWorkDir] = useState("");
  const [timeout, setTimeout] = useState(30);
  const [plugins, setPlugins] = useState<string[]>([]);
  const [skills, setSkills] = useState<string[]>([]);
  const [pluginInput, setPluginInput] = useState("");
  const [skillInput, setSkillInput] = useState("");
  const [status, setStatus] = useState("");

  const handleSubmit = async () => {
    if (!name.trim()) { setStatus("Name is required"); return; }
    try {
      await createAgent({
        name: name.trim(),
        icon,
        description,
        executionType: execType,
        systemPrompt: execType === "Llm" ? systemPrompt : undefined,
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

  const addPlugin = () => {
    if (pluginInput.trim() && !plugins.includes(pluginInput.trim())) {
      setPlugins([...plugins, pluginInput.trim()]);
      setPluginInput("");
    }
  };

  const addSkill = () => {
    if (skillInput.trim() && !skills.includes(skillInput.trim())) {
      setSkills([...skills, skillInput.trim()]);
      setSkillInput("");
    }
  };

  return (
    <div className="card">
      <h2>➕ Create Agent</h2>

      <div style={{ marginBottom: "0.75rem" }}>
        <label className="form-label">Execution Type</label>
        <div style={{ display: "flex", gap: "0.5rem" }}>
          <button
            className={`btn btn-sm ${execType === "Llm" ? "btn-primary" : "btn-outline"}`}
            onClick={() => setExecType("Llm")}
          >
            🤖 LLM Agent
          </button>
          <button
            className={`btn btn-sm ${execType === "External" ? "btn-primary" : "btn-outline"}`}
            onClick={() => setExecType("External")}
          >
            ⌨️ External (CLI / Script)
          </button>
        </div>
      </div>

      <div style={{ display: "flex", gap: "0.75rem", marginBottom: "0.75rem" }}>
        <input
          style={{ flex: 1 }}
          placeholder="Agent name"
          value={name}
          onChange={(e) => setName(e.target.value)}
        />
        <input
          style={{ width: 80 }}
          placeholder="Icon"
          value={icon}
          onChange={(e) => setIcon(e.target.value)}
        />
      </div>

      {execType === "Llm" && (
        <textarea
          placeholder="System prompt — define the agent's role and behavior..."
          value={systemPrompt}
          onChange={(e) => setSystemPrompt(e.target.value)}
        />
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
          <div className="protocol-info">
            <div style={{ fontWeight: 600, marginBottom: "0.25rem" }}>📋 stdin/stdout JSON Protocol</div>
            <div style={{ fontSize: "0.75rem", color: "var(--text-muted)", lineHeight: 1.5 }}>
              Your script receives <code>{`{"input":"...","parameters":{...},"userId":"..."}`}</code> on <strong>stdin</strong>.<br />
              It must write <code>{`{"success":true,"message":"...","data":...}`}</code> to <strong>stdout</strong>.<br />
              Exit code 0 = success, non-zero = failure.
            </div>
          </div>
        </>
      )}

      <textarea
        placeholder="Description"
        value={description}
        onChange={(e) => setDescription(e.target.value)}
        style={{ minHeight: "50px" }}
      />

      <div style={{ marginBottom: "0.75rem" }}>
        <label className="form-label">Plugins</label>
        <div style={{ display: "flex", gap: "0.5rem", flexWrap: "wrap", alignItems: "center" }}>
          {plugins.map((p) => (
            <span key={p} className="badge badge-private" style={{ padding: "4px 10px", cursor: "pointer" }} onClick={() => setPlugins(plugins.filter((x) => x !== p))}>
              {p} ✕
            </span>
          ))}
          <input
            style={{ width: "auto", flex: 1, minWidth: 120, marginBottom: 0 }}
            placeholder="Plugin name"
            value={pluginInput}
            onChange={(e) => setPluginInput(e.target.value)}
            onKeyDown={(e) => e.key === "Enter" && addPlugin()}
          />
          <button className="pipeline-add" style={{ padding: "2px 8px" }} onClick={addPlugin}>+ Add</button>
        </div>
      </div>

      <div style={{ marginBottom: "0.75rem" }}>
        <label className="form-label">Skills (injected into system prompt)</label>
        <div style={{ display: "flex", gap: "0.5rem", flexWrap: "wrap", alignItems: "center" }}>
          {skills.map((s) => (
            <span key={s} className="badge badge-public" style={{ padding: "4px 10px", cursor: "pointer" }} onClick={() => setSkills(skills.filter((x) => x !== s))}>
              {s} ✕
            </span>
          ))}
          <input
            style={{ width: "auto", flex: 1, minWidth: 120, marginBottom: 0 }}
            placeholder="Skill name"
            value={skillInput}
            onChange={(e) => setSkillInput(e.target.value)}
            onKeyDown={(e) => e.key === "Enter" && addSkill()}
          />
          <button className="pipeline-add" style={{ padding: "2px 8px" }} onClick={addSkill}>+ Add</button>
        </div>
      </div>

      <button className="btn btn-primary" onClick={handleSubmit}>Create Agent</button>

      {status && (
        <div className="result-box result-success" style={{ marginTop: "0.75rem" }}>{status}</div>
      )}
    </div>
  );
}
