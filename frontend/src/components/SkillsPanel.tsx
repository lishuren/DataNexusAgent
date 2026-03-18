import { useState } from "react";
import type { Skill } from "@/types/api";
import { createSkill, publishSkill } from "@/services/api";

interface Props {
  skills: Skill[];
  onRefresh: () => void;
}

export function SkillsPanel({ skills, onRefresh }: Props) {
  const [name, setName] = useState("");
  const [instructions, setInstructions] = useState("");
  const [status, setStatus] = useState<string | null>(null);

  const handleCreate = async () => {
    if (!name.trim() || !instructions.trim()) return;
    try {
      await createSkill(name, instructions);
      setName("");
      setInstructions("");
      setStatus(`Skill "${name}" created.`);
      onRefresh();
    } catch (e) {
      setStatus(`Error: ${e instanceof Error ? e.message : String(e)}`);
    }
  };

  const handlePublish = async (skillName: string) => {
    try {
      await publishSkill(skillName);
      setStatus(`Skill "${skillName}" published to marketplace.`);
      onRefresh();
    } catch (e) {
      setStatus(`Error: ${e instanceof Error ? e.message : String(e)}`);
    }
  };

  return (
    <>
      <div className="card">
        <h2>Your Skills</h2>
        {skills.length === 0 ? (
          <p style={{ color: "var(--text-muted)", fontSize: "0.875rem" }}>
            No skills yet. Create one below.
          </p>
        ) : (
          <ul className="skill-list">
            {skills.map((s) => (
              <li key={`${s.scope}-${s.name}`}>
                <span>
                  {s.name}{" "}
                  <span
                    className={`badge ${s.scope === "Public" ? "badge-public" : "badge-private"}`}
                  >
                    {s.scope}
                  </span>
                </span>
                {s.scope === "Private" && (
                  <button className="btn btn-sm btn-primary" onClick={() => handlePublish(s.name)}>
                    Publish
                  </button>
                )}
              </li>
            ))}
          </ul>
        )}
      </div>

      <div className="card">
        <h2>Create Skill</h2>
        <input
          placeholder="Skill name (e.g. custom-parser)"
          value={name}
          onChange={(e) => setName(e.target.value)}
        />
        <textarea
          placeholder="Skill instructions in markdown..."
          value={instructions}
          onChange={(e) => setInstructions(e.target.value)}
        />
        <button className="btn btn-primary" onClick={handleCreate}>
          Create Skill
        </button>
      </div>

      {status && (
        <div className="result-box result-success">{status}</div>
      )}
    </>
  );
}
