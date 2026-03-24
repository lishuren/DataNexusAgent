import { useCallback, useEffect, useState } from "react";
import type { Skill } from "@/types/api";
import { listSkills, createSkill, publishSkill, unpublishSkill, updateSkill, deleteSkill, cloneSkill } from "@/services/api";
import { getUserId } from "@/services/auth";

// ---------------------------------------------------------------------------
// Skill authoring help panel
// ---------------------------------------------------------------------------

const SECTION_TEMPLATES: { label: string; description: string; template: string }[] = [
  {
    label: "Purpose",
    description: "What this skill teaches the LLM to do.",
    template: "## Purpose\n\nDescribe the goal of this skill in one or two sentences.",
  },
  {
    label: "Rules",
    description: "Numbered or bulleted instructions the LLM must follow.",
    template: "## Rules\n\n- Rule one.\n- Rule two.\n- Rule three.",
  },
  {
    label: "Type Mapping",
    description: "A table mapping input patterns to output types.",
    template: "## Type Mapping\n\n| Input Pattern | Output Type |\n| ------------- | ----------- |\n| Example A     | Type X      |\n| Example B     | Type Y      |",
  },
  {
    label: "Examples",
    description: "Concrete input → output pairs to guide the model.",
    template: "## Examples\n\n**Input:** `raw value here`\n**Output:** `transformed value here`",
  },
  {
    label: "Constraints",
    description: "Hard limits or things the LLM must never do.",
    template: "## Constraints\n\n- Never include X.\n- Always output valid JSON.\n- Do not infer missing fields — use null.",
  },
];

function SkillHelpPanel({
  onInsert,
}: {
  onInsert: (text: string) => void;
}) {
  const [open, setOpen] = useState(false);

  return (
    <div style={{ marginBottom: "0.5rem" }}>
      <div style={{ display: "flex", alignItems: "center", gap: "0.5rem", marginBottom: "0.35rem" }}>
        <label className="form-label" style={{ marginBottom: 0 }}>
          Instructions{" "}
          <span style={{ fontWeight: 400, color: "var(--text-muted)" }}>
            (markdown — injected verbatim into the agent's system prompt)
          </span>
        </label>
        <button
          type="button"
          title="Show skill authoring tips"
          onClick={() => setOpen((o) => !o)}
          style={{
            background: "none",
            border: "1px solid var(--border)",
            borderRadius: "50%",
            width: 22,
            height: 22,
            cursor: "pointer",
            fontSize: "0.75rem",
            lineHeight: 1,
            color: open ? "var(--primary)" : "var(--text-muted)",
            padding: 0,
            flexShrink: 0,
          }}
        >
          ?
        </button>
      </div>

      {open && (
        <div
          style={{
            border: "1px solid var(--border)",
            borderRadius: "0.5rem",
            padding: "0.75rem",
            marginBottom: "0.5rem",
            background: "var(--surface)",
          }}
        >
          {/* Tips row */}
          <div style={{ fontWeight: 600, marginBottom: "0.5rem", fontSize: "0.85rem" }}>
            Writing good skills
          </div>
          <ul style={{ fontSize: "0.8rem", color: "var(--text-muted)", paddingLeft: "1.2rem", marginBottom: "0.75rem", lineHeight: 1.7 }}>
            <li>Write clear, imperative sentences — the LLM follows them literally.</li>
            <li>Use <strong>Rules</strong> sections for transformations the model must always apply.</li>
            <li>Use <strong>Examples</strong> to show exact input → output pairs.</li>
            <li>Keep skills focused on one domain — combine multiple skills on the agent instead.</li>
            <li>Skills are passive knowledge only — they cannot trigger plugins or API calls.</li>
          </ul>

          {/* Section template inserts */}
          <div style={{ fontWeight: 600, marginBottom: "0.4rem", fontSize: "0.85rem" }}>
            Insert a section template
          </div>
          <div style={{ display: "flex", flexDirection: "column", gap: "0.35rem" }}>
            {SECTION_TEMPLATES.map((st) => (
              <div
                key={st.label}
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
                    minWidth: 90,
                    textAlign: "center",
                    fontWeight: 600,
                    flexShrink: 0,
                  }}
                >
                  {st.label}
                </code>
                <span style={{ flex: 1, color: "var(--text-muted)" }}>{st.description}</span>
                <button
                  type="button"
                  className="btn btn-outline btn-sm"
                  style={{ fontSize: "0.75rem", padding: "2px 8px", flexShrink: 0 }}
                  onClick={() => onInsert(st.template)}
                >
                  + Insert
                </button>
              </div>
            ))}
          </div>
        </div>
      )}
    </div>
  );
}


export default function SkillsPage() {
  const [skills, setSkills] = useState<Skill[]>([]);
  const [name, setName] = useState("");
  const [instructions, setInstructions] = useState("");
  const [status, setStatus] = useState<string | null>(null);
  const [editingSkillId, setEditingSkillId] = useState<number | null>(null);
  const userId = getUserId();

  const refresh = useCallback(async () => {
    try {
      setSkills(await listSkills());
    } catch (e) {
      console.warn("Failed to load skills:", e);
    }
  }, []);

  useEffect(() => { refresh(); }, [refresh]);

  const handleCreate = async () => {
    if (!name.trim() || !instructions.trim()) return;
    try {
      if (editingSkillId) {
        await updateSkill(editingSkillId, name, instructions);
        setStatus(`Skill "${name}" updated.`);
      } else {
        await createSkill(name, instructions);
        setStatus(`Skill "${name}" created.`);
      }
      setName("");
      setInstructions("");
      setEditingSkillId(null);
      refresh();
    } catch (e) {
      setStatus(`Error: ${e instanceof Error ? e.message : String(e)}`);
    }
  };

  const handleEdit = (skill: Skill) => {
    setEditingSkillId(skill.id);
    setName(skill.name);
    setInstructions(skill.instructions ?? "");
  };

  const handleDelete = async (skillId: number) => {
    try {
      await deleteSkill(skillId);
      if (editingSkillId === skillId) {
        setEditingSkillId(null);
        setName("");
        setInstructions("");
      }
      refresh();
    } catch (e) {
      setStatus(`Error: ${e instanceof Error ? e.message : String(e)}`);
    }
  };

  const handleClone = async (skill: Skill) => {
    const newName = window.prompt("Clone skill name", skill.name);
    if (!newName?.trim()) return;
    try {
      await cloneSkill(skill.id, newName.trim());
      refresh();
    } catch (e) {
      setStatus(`Error: ${e instanceof Error ? e.message : String(e)}`);
    }
  };

  const handlePublish = async (skillId: number, skillName: string) => {
    try {
      await publishSkill(skillId);
      setStatus(`Skill "${skillName}" published to marketplace.`);
      refresh();
    } catch (e) {
      setStatus(`Error: ${e instanceof Error ? e.message : String(e)}`);
    }
  };

  const handleUnpublish = async (skillId: number, skillName: string) => {
    try {
      await unpublishSkill(skillId);
      setStatus(`Skill "${skillName}" unpublished.`);
      refresh();
    } catch (e) {
      setStatus(`Error: ${e instanceof Error ? e.message : String(e)}`);
    }
  };

  return (
    <>
      <div className="card">
        <h2>📋 Your Skills</h2>
        {skills.length === 0 ? (
          <p style={{ color: "var(--text-muted)", fontSize: "0.875rem" }}>
            No skills yet. Create one below.
          </p>
        ) : (
          <ul className="skill-list">
            {skills.map((s) => (
              <li key={s.id}>
                <span>
                  {s.name}{" "}
                  <span className={`badge ${s.scope === "Public" ? "badge-public" : "badge-private"}`}>
                    {s.scope}
                  </span>
                </span>
                <span style={{ display: "flex", gap: "0.5rem" }}>
                  {s.scope === "Private" && s.ownerId === userId && (
                    <button className="btn btn-sm btn-primary" onClick={() => handlePublish(s.id, s.name)}>
                      Publish
                    </button>
                  )}
                  {s.scope === "Public" && s.publishedByUserId === userId && (
                    <button className="btn btn-sm btn-outline" onClick={() => handleUnpublish(s.id, s.name)}>
                      Unpublish
                    </button>
                  )}
                  {s.scope === "Private" && s.ownerId === userId && (
                    <button className="btn btn-sm btn-outline" onClick={() => handleEdit(s)}>
                      Edit
                    </button>
                  )}
                  {s.scope === "Private" && s.ownerId === userId && (
                    <button className="btn btn-sm btn-outline btn-outline-danger" onClick={() => handleDelete(s.id)}>
                      Delete
                    </button>
                  )}
                  <button className="btn btn-sm btn-outline" onClick={() => handleClone(s)}>
                    Clone
                  </button>
                </span>
              </li>
            ))}
          </ul>
        )}
      </div>

      <div className="card">
        <h2>{editingSkillId ? "✏️ Edit Skill" : "➕ Create Skill"}</h2>
        <input
          placeholder="Skill name (e.g. custom-parser)"
          value={name}
          onChange={(e) => setName(e.target.value)}
        />
        <SkillHelpPanel
          onInsert={(text) =>
            setInstructions((prev) =>
              prev ? `${prev}\n\n${text}` : text
            )
          }
        />
        <textarea
          placeholder="Skill instructions in markdown..."
          value={instructions}
          onChange={(e) => setInstructions(e.target.value)}
          style={{ minHeight: "280px", marginTop: 0 }}
        />
        <div style={{ display: "flex", gap: "0.75rem", flexWrap: "wrap" }}>
          <button className="btn btn-primary" onClick={handleCreate}>
            {editingSkillId ? "Save Changes" : "Create Skill"}
          </button>
          {editingSkillId && (
            <button
              className="btn btn-outline btn-sm"
              onClick={() => { setEditingSkillId(null); setName(""); setInstructions(""); }}
            >
              Cancel
            </button>
          )}
        </div>
      </div>

      {status && <div className="result-box result-success">{status}</div>}
    </>
  );
}
