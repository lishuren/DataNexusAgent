import { useCallback, useEffect, useState } from "react";
import type { Agent, Orchestration, Skill } from "@/types/api";
import { listPublicAgents, listPublicSkills, listPublicOrchestrations, cloneOrchestration } from "@/services/api";
import { AgentCard } from "@/components/AgentCard";

type SubTab = "agents" | "skills" | "plugins" | "orchestrations";

export default function MarketplacePage() {
  const [subTab, setSubTab] = useState<SubTab>("agents");
  const [agents, setAgents] = useState<Agent[]>([]);
  const [skills, setSkills] = useState<Skill[]>([]);
  const [orchestrations, setOrchestrations] = useState<Orchestration[]>([]);
  const plugins = [
    {
      name: "InputProcessor",
      description: "Parses Excel/CSV/JSON input into structured JSON for the agent.",
    },
    {
      name: "OutputIntegrator",
      description: "Validates output schema and executes API/database writes.",
    },
  ];

  const refresh = useCallback(async () => {
    try {
      const [a, s, o] = await Promise.all([listPublicAgents(), listPublicSkills(), listPublicOrchestrations()]);
      setAgents(a);
      setSkills(s);
      setOrchestrations(o);
    } catch (e) {
      console.warn("Failed to load marketplace data:", e);
    }
  }, []);

  useEffect(() => { refresh(); }, [refresh]);

  return (
    <>
      <div className="sub-tabs">
        <button
          className={`sub-tab ${subTab === "agents" ? "active" : ""}`}
          onClick={() => setSubTab("agents")}
        >
          🤖 Agents
        </button>
        <button
          className={`sub-tab ${subTab === "skills" ? "active" : ""}`}
          onClick={() => setSubTab("skills")}
        >
          📋 Skills
        </button>
        <button
          className={`sub-tab ${subTab === "plugins" ? "active" : ""}`}
          onClick={() => setSubTab("plugins")}
        >
          🧩 Plugins
        </button>
        <button
          className={`sub-tab ${subTab === "orchestrations" ? "active" : ""}`}
          onClick={() => setSubTab("orchestrations")}
        >
          🗂️ Orchestrations
        </button>
      </div>

      {subTab === "agents" && (
        <div className="agent-grid">
          {agents.length === 0 ? (
            <p style={{ color: "var(--text-muted)", fontSize: "0.875rem" }}>No public agents available yet.</p>
          ) : (
            agents.map((a) => <AgentCard key={a.id} agent={a} />)
          )}
        </div>
      )}

      {subTab === "skills" && (
        <div className="card" style={{ marginTop: 0 }}>
          {skills.length === 0 ? (
            <p style={{ color: "var(--text-muted)", fontSize: "0.875rem" }}>No public skills available yet.</p>
          ) : (
            <ul className="skill-list">
              {skills.map((s) => (
                <li key={s.id}>
                  <span>
                    {s.name} <span className="badge badge-public">Public</span>
                  </span>
                </li>
              ))}
            </ul>
          )}
        </div>
      )}

      {subTab === "plugins" && (
        <div className="card" style={{ marginTop: 0 }}>
          <div style={{ display: "grid", gap: "0.75rem" }}>
            {plugins.map((p) => (
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
      )}

      {subTab === "orchestrations" && (
        <div className="card" style={{ marginTop: 0 }}>
          {orchestrations.length === 0 ? (
            <p style={{ color: "var(--text-muted)", fontSize: "0.875rem" }}>No public orchestrations available yet.</p>
          ) : (
            <ul className="skill-list">
              {orchestrations.map((o) => (
                <li key={o.id}>
                  <span style={{ display: "flex", flexDirection: "column", gap: "2px" }}>
                    <span style={{ fontWeight: 600, fontSize: "0.85rem" }}>
                      🗂️ {o.name}
                      <span className="badge badge-public" style={{ marginLeft: "6px" }}>
                        {o.status}
                      </span>
                    </span>
                    <span style={{ fontSize: "0.75rem", color: "var(--text-muted)" }}>
                      {o.goal.length > 100 ? o.goal.slice(0, 100) + "…" : o.goal}
                    </span>
                    <span style={{ fontSize: "0.7rem", color: "var(--text-muted)" }}>
                      {o.steps.length} steps: {o.steps.map((s) => s.agentName).join(" → ")}
                    </span>
                  </span>
                  <button
                    className="btn btn-sm btn-outline"
                    onClick={async () => {
                      const name = window.prompt("Clone name:", o.name);
                      if (!name?.trim()) return;
                      try {
                        await cloneOrchestration(o.id, name.trim());
                        alert("Cloned! Check your Process page.");
                      } catch (e) {
                        alert(e instanceof Error ? e.message : String(e));
                      }
                    }}
                  >
                    Clone
                  </button>
                </li>
              ))}
            </ul>
          )}
        </div>
      )}
    </>
  );
}
