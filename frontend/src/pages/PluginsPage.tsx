import { useCallback, useEffect, useState } from "react";
import { Link } from "react-router-dom";
import type { Agent } from "@/types/api";
import { listAgents } from "@/services/api";

interface PluginInfo {
  name: string;
  icon: string;
  phase: string;
  description: string;
  details: string;
}

const PLUGIN_CATALOG: PluginInfo[] = [
  {
    name: "InputProcessor",
    icon: "📥",
    phase: "Pre-LLM",
    description: "Parses Excel, CSV, JSON, and text input into structured JSON for the agent.",
    details:
      "Runs before the LLM call. Handles base64 data-URL uploads (including gzip-compressed payloads from the browser), " +
      "HTTPS file downloads, Excel workbook parsing via ClosedXML, CSV/TSV parsing, and JSON normalization. " +
      "The parsed output replaces the user message so the LLM receives clean structured data.",
  },
  {
    name: "OutputIntegrator",
    icon: "📤",
    phase: "Post-LLM",
    description: "Validates output schemas and executes API calls or database writes.",
    details:
      "Runs after the LLM call. Validates the LLM response against an optional destination schema, " +
      "then routes the output to an HTTPS API endpoint or database. Supports passthrough mode for " +
      "format-only outputs (JSON, CSV, SQL). SSRF-protected — only HTTPS endpoints are permitted.",
  },
];

function normalizePlugins(plugins: string[] | string): string[] {
  if (Array.isArray(plugins)) return plugins;
  if (typeof plugins === "string" && plugins.trim())
    return plugins.split(",").map((p) => p.trim()).filter(Boolean);
  return [];
}

export default function PluginsPage() {
  const [agents, setAgents] = useState<Agent[]>([]);
  const [expandedPlugin, setExpandedPlugin] = useState<string | null>(null);

  const refresh = useCallback(async () => {
    try {
      setAgents(await listAgents());
    } catch (e) {
      console.warn("Failed to load agents:", e);
    }
  }, []);

  useEffect(() => { refresh(); }, [refresh]);

  const agentsByPlugin = (pluginName: string): Agent[] =>
    agents.filter((a) => normalizePlugins(a.plugins).includes(pluginName));

  return (
    <>
      <div className="card">
        <h2>🧩 Plugins</h2>
        <p style={{ color: "var(--text-muted)", fontSize: "0.875rem", marginBottom: "1rem" }}>
          Plugins are compiled C# modules that run before or after the LLM call.
          They are assigned to agents via the <strong>Plugins</strong> field on the{" "}
          <Link to="/agents" style={{ color: "var(--primary)" }}>Agents page</Link>.
        </p>

        <div style={{ display: "flex", flexDirection: "column", gap: "1rem" }}>
          {PLUGIN_CATALOG.map((plugin) => {
            const usedBy = agentsByPlugin(plugin.name);
            const isExpanded = expandedPlugin === plugin.name;

            return (
              <div
                key={plugin.name}
                style={{
                  border: "1px solid var(--border)",
                  borderRadius: "0.5rem",
                  padding: "1rem",
                  background: "var(--surface)",
                }}
              >
                {/* Header row */}
                <div style={{ display: "flex", alignItems: "center", gap: "0.75rem", marginBottom: "0.5rem" }}>
                  <span style={{ fontSize: "1.5rem" }}>{plugin.icon}</span>
                  <div style={{ flex: 1 }}>
                    <div style={{ fontWeight: 600, fontSize: "1rem" }}>
                      {plugin.name}
                      <span
                        className="badge"
                        style={{
                          marginLeft: "0.5rem",
                          background: plugin.phase === "Pre-LLM"
                            ? "rgba(99, 102, 241, 0.15)"
                            : "rgba(236, 72, 153, 0.15)",
                          color: plugin.phase === "Pre-LLM"
                            ? "var(--primary, #6366f1)"
                            : "#ec4899",
                        }}
                      >
                        {plugin.phase}
                      </span>
                    </div>
                    <div style={{ fontSize: "0.85rem", color: "var(--text-muted)", marginTop: "0.15rem" }}>
                      {plugin.description}
                    </div>
                  </div>
                  <button
                    className="btn btn-sm btn-outline"
                    onClick={() => setExpandedPlugin(isExpanded ? null : plugin.name)}
                    title={isExpanded ? "Hide details" : "Show details"}
                  >
                    {isExpanded ? "▲ Less" : "▼ More"}
                  </button>
                </div>

                {/* Expanded details */}
                {isExpanded && (
                  <div
                    style={{
                      fontSize: "0.825rem",
                      color: "var(--text-muted)",
                      lineHeight: 1.6,
                      padding: "0.75rem",
                      background: "rgba(var(--primary-rgb, 99,102,241), 0.04)",
                      borderRadius: "0.375rem",
                      marginBottom: "0.75rem",
                    }}
                  >
                    {plugin.details}
                  </div>
                )}

                {/* Used-by agents */}
                <div style={{ fontSize: "0.8rem", color: "var(--text-muted)" }}>
                  <strong>Used by:</strong>{" "}
                  {usedBy.length === 0 ? (
                    <span>No agents currently use this plugin.</span>
                  ) : (
                    usedBy.map((a, i) => (
                      <span key={a.id}>
                        {i > 0 && ", "}
                        <span title={a.description}>
                          {a.icon} {a.name}
                          {a.scope === "Public" && (
                            <span className="badge badge-public" style={{ marginLeft: "0.25rem", fontSize: "0.65rem" }}>
                              Public
                            </span>
                          )}
                        </span>
                      </span>
                    ))
                  )}
                </div>
              </div>
            );
          })}
        </div>
      </div>

      <div className="card">
        <h2>💡 How Plugins Work</h2>
        <div style={{ fontSize: "0.875rem", lineHeight: 1.8 }}>
          <p>
            Each agent can have zero, one, or both plugins assigned. The execution flow is:
          </p>
          <div
            style={{
              display: "flex",
              alignItems: "center",
              gap: "0.5rem",
              padding: "0.75rem",
              background: "var(--surface)",
              border: "1px solid var(--border)",
              borderRadius: "0.5rem",
              justifyContent: "center",
              flexWrap: "wrap",
              fontWeight: 500,
              fontSize: "0.9rem",
              marginBottom: "0.75rem",
            }}
          >
            <span>📥 InputProcessor</span>
            <span style={{ color: "var(--text-muted)" }}>→</span>
            <span>🤖 LLM</span>
            <span style={{ color: "var(--text-muted)" }}>→</span>
            <span>📤 OutputIntegrator</span>
          </div>
          <ul style={{ paddingLeft: "1.2rem", color: "var(--text-muted)" }}>
            <li><strong>Skills</strong> shape how the LLM thinks (passive markdown knowledge).</li>
            <li><strong>Plugins</strong> give the agent ability to act (file parsing, API calls, DB writes).</li>
            <li>Skills cannot invoke plugins — this is a deliberate security boundary.</li>
          </ul>
          <p style={{ color: "var(--text-muted)", marginTop: "0.5rem" }}>
            To change which plugins an agent uses, go to the{" "}
            <Link to="/agents" style={{ color: "var(--primary)" }}>Agents page</Link> and edit the agent&apos;s Plugins field.
          </p>
        </div>
      </div>
    </>
  );
}
