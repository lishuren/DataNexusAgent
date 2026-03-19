import { useCallback, useEffect, useState } from "react";
import type { Agent, Skill } from "@/types/api";
import { listPublicAgents, listPublicSkills } from "@/services/api";
import { AgentCard } from "@/components/AgentCard";

type SubTab = "agents" | "skills";

export default function MarketplacePage() {
  const [subTab, setSubTab] = useState<SubTab>("agents");
  const [agents, setAgents] = useState<Agent[]>([]);
  const [skills, setSkills] = useState<Skill[]>([]);

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
                <li key={s.name}>
                  <span>
                    {s.name} <span className="badge badge-public">Public</span>
                  </span>
                </li>
              ))}
            </ul>
          )}
        </div>
      )}
    </>
  );
}
