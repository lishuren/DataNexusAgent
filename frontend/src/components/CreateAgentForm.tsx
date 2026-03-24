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

// ---------------------------------------------------------------------------
// UI Schema field type catalogue
// ---------------------------------------------------------------------------

const FIELD_TYPES: {
  type: string;
  label: string;
  description: string;
  extras: string;
  template: Record<string, unknown>;
}[] = [
  {
    type: "file",
    label: "File upload",
    description: "Renders a drag-and-drop file zone.",
    extras: 'accept: ".xlsx,.csv"',
    template: { key: "file", label: "Upload File", type: "file", accept: ".xlsx,.csv,.json", required: true },
  },
  {
    type: "text",
    label: "Text input",
    description: "Single-line text field.",
    extras: "placeholder",
    template: { key: "myField", label: "My Field", type: "text", placeholder: "Enter value…" },
  },
  {
    type: "textarea",
    label: "Textarea",
    description: "Multi-line text area.",
    extras: "placeholder",
    template: { key: "notes", label: "Notes", type: "textarea", placeholder: "Type here…" },
  },
  {
    type: "url",
    label: "URL input",
    description: "URL-validated text field.",
    extras: "placeholder",
    template: { key: "endpoint", label: "Endpoint URL", type: "url", placeholder: "https://…" },
  },
  {
    type: "select",
    label: "Dropdown",
    description: "Pick-one from a fixed list.",
    extras: 'options: ["A","B","C"]',
    template: { key: "format", label: "Output Format", type: "select", options: ["JSON", "CSV", "SQL"] },
  },
  {
    type: "number",
    label: "Number",
    description: "Numeric input field.",
    extras: "placeholder",
    template: { key: "limit", label: "Row Limit", type: "number", placeholder: "100" },
  },
  {
    type: "toggle",
    label: "Toggle (checkbox)",
    description: "Boolean checkbox with a label.",
    extras: 'default: "true" | "false"',
    template: { key: "verbose", label: "Verbose Output", type: "toggle", default: "false" },
  },
];

// ---------------------------------------------------------------------------
// UiSchemaEditor — textarea + collapsible help panel
// ---------------------------------------------------------------------------

function UiSchemaEditor({
  value,
  onChange,
}: {
  value: string | undefined;
  onChange: (v: string | undefined) => void;
}) {
  const [helpOpen, setHelpOpen] = useState(false);

  const insertField = (template: Record<string, unknown>) => {
    let current: unknown[] = [];
    try {
      const parsed = JSON.parse(value ?? "[]");
      if (Array.isArray(parsed)) current = parsed;
    } catch {
      // ignore parse errors — just append
    }
    current.push(template);
    onChange(JSON.stringify(current, null, 2));
  };

  return (
    <div style={{ marginBottom: "0.75rem" }}>
      {/* Label row */}
      <div style={{ display: "flex", alignItems: "center", gap: "0.5rem", marginBottom: "0.35rem" }}>
        <label className="form-label" style={{ marginBottom: 0 }}>
          UI Schema{" "}
          <span style={{ fontWeight: 400, color: "var(--text-muted)" }}>
            (JSON array — defines form fields on the Process page)
          </span>
        </label>
        <button
          type="button"
          title="Show available field types"
          onClick={() => setHelpOpen((o) => !o)}
          style={{
            background: "none",
            border: "1px solid var(--border)",
            borderRadius: "50%",
            width: 22,
            height: 22,
            cursor: "pointer",
            fontSize: "0.75rem",
            lineHeight: 1,
            color: helpOpen ? "var(--primary)" : "var(--text-muted)",
            padding: 0,
            flexShrink: 0,
          }}
        >
          ?
        </button>
      </div>

      {/* Collapsible help panel */}
      {helpOpen && (
        <div
          style={{
            border: "1px solid var(--border)",
            borderRadius: "0.5rem",
            padding: "0.75rem",
            marginBottom: "0.5rem",
            background: "var(--surface)",
          }}
        >
          <div style={{ fontWeight: 600, marginBottom: "0.5rem", fontSize: "0.85rem" }}>
            Available field types — click <strong>+ Insert</strong> to add a template
          </div>
          <div style={{ display: "flex", flexDirection: "column", gap: "0.4rem" }}>
            {FIELD_TYPES.map((ft) => (
              <div
                key={ft.type}
                style={{
                  display: "flex",
                  alignItems: "center",
                  gap: "0.6rem",
                  fontSize: "0.8rem",
                  borderBottom: "1px solid var(--border)",
                  paddingBottom: "0.35rem",
                }}
              >
                <code
                  style={{
                    background: "rgba(var(--primary-rgb), 0.15)",
                    color: "var(--primary)",
                    borderRadius: 4,
                    padding: "2px 6px",
                    minWidth: 72,
                    textAlign: "center",
                    fontWeight: 600,
                    flexShrink: 0,
                  }}
                >
                  {ft.type}
                </code>
                <div style={{ flex: 1 }}>
                  <span style={{ fontWeight: 500 }}>{ft.label}</span>
                  {" — "}
                  <span style={{ color: "var(--text-muted)" }}>{ft.description}</span>
                  {" "}
                  <span style={{ color: "var(--text-muted)", fontStyle: "italic" }}>
                    Extra: {ft.extras}
                  </span>
                </div>
                <button
                  type="button"
                  className="btn btn-outline btn-sm"
                  style={{ fontSize: "0.75rem", padding: "2px 8px", flexShrink: 0 }}
                  onClick={() => insertField(ft.template)}
                >
                  + Insert
                </button>
              </div>
            ))}
          </div>
          <div style={{ marginTop: "0.5rem", fontSize: "0.75rem", color: "var(--text-muted)" }}>
            All fields support{" "}
            <code style={{ background: "rgba(var(--primary-rgb), 0.15)", color: "var(--primary)", borderRadius: 3, padding: "1px 4px" }}>key</code>{" "}(unique id),{" "}
            <code style={{ background: "rgba(var(--primary-rgb), 0.15)", color: "var(--primary)", borderRadius: 3, padding: "1px 4px" }}>label</code>,{" "}
            <code style={{ background: "rgba(var(--primary-rgb), 0.15)", color: "var(--primary)", borderRadius: 3, padding: "1px 4px" }}>type</code>,{" "}
            and optional{" "}
            <code style={{ background: "rgba(var(--primary-rgb), 0.15)", color: "var(--primary)", borderRadius: 3, padding: "1px 4px" }}>required: true</code>.
          </div>
        </div>
      )}

      {/* Raw JSON textarea */}
      <textarea
        placeholder={'[{"key":"file","label":"Upload file","type":"file","accept":".xlsx"}]'}
        value={value ?? ""}
        onChange={(e) => onChange(e.target.value || undefined)}
        style={{ minHeight: "80px", fontFamily: "monospace", fontSize: "0.8rem", marginBottom: 0 }}
      />
    </div>
  );
}

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
      <UiSchemaEditor value={uiSchema} onChange={setUiSchema} />

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
