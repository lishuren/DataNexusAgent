import { useCallback, useEffect, useState } from "react";
import type { Agent, Skill } from "@/types/api";
import { listPublicAgents, listPublicSkills } from "@/services/api";
import { AgentCard } from "@/components/AgentCard";

type SubTab = "agents" | "skills" | "plugins";

export default function MarketplacePage() {
  const [subTab, setSubTab] = useState<SubTab>("agents");
  const [agents, setAgents] = useState<Agent[]>([]);
  const [skills, setSkills] = useState<Skill[]>([]);
  const plugins = [
    {
      name: "ExcelParser",
      description: "Parses Excel/CSV/JSON input into structured JSON for the agent.",
    },
    {
      name: "OutputIntegrator",
      description: "Validates output schema and executes API/database writes.",
    },
  ];

  const refresh = useCallback(async () => {
    try {
      const [a, s] = await Promise.all([listPublicAgents(), listPublicSkills()]);
      setAgents(a);
      setSkills(s);
    } catch {
      /* ignore */
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
    </>
  );
}
