import { useCallback, useEffect, useState } from "react";
import { SkillsPanel } from "@/components/SkillsPanel";
import { ProcessingPanel } from "@/components/ProcessingPanel";
import { listSkills, listPublicSkills } from "@/services/api";
import { getDisplayName, logout } from "@/services/auth";
import type { Skill } from "@/types/api";

type Tab = "process" | "skills" | "marketplace";

export default function App() {
  const [tab, setTab] = useState<Tab>("process");
  const [skills, setSkills] = useState<Skill[]>([]);
  const [publicSkills, setPublicSkills] = useState<Skill[]>([]);
  const displayName = getDisplayName();

  const refreshSkills = useCallback(async () => {
    try {
      const [s, p] = await Promise.all([listSkills(), listPublicSkills()]);
      setSkills(s);
      setPublicSkills(p);
    } catch {
      /* auth may not be ready yet */
    }
  }, []);

  useEffect(() => {
    refreshSkills();
  }, [refreshSkills]);

  return (
    <div className="app">
      <header>
        <h1>DataNexus</h1>
        <nav>
          <a
            href="#"
            className={tab === "process" ? "active" : ""}
            onClick={() => setTab("process")}
          >
            Process
          </a>
          <a
            href="#"
            className={tab === "skills" ? "active" : ""}
            onClick={() => setTab("skills")}
          >
            My Skills
          </a>
          <a
            href="#"
            className={tab === "marketplace" ? "active" : ""}
            onClick={() => setTab("marketplace")}
          >
            Marketplace
          </a>
          <span className="user-info">{displayName}</span>
          <button className="btn btn-sm btn-danger" onClick={logout}>
            Sign out
          </button>
        </nav>
      </header>

      <main>
        {tab === "process" && <ProcessingPanel />}
        {tab === "skills" && <SkillsPanel skills={skills} onRefresh={refreshSkills} />}
        {tab === "marketplace" && (
          <div className="card">
            <h2>Public Skills Marketplace</h2>
            {publicSkills.length === 0 ? (
              <p style={{ color: "var(--text-muted)", fontSize: "0.875rem" }}>
                No public skills available yet.
              </p>
            ) : (
              <ul className="skill-list">
                {publicSkills.map((s) => (
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
      </main>
    </div>
  );
}
