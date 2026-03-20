import { useCallback, useEffect, useState } from "react";
import type { Skill } from "@/types/api";
import { listSkills, createSkill, publishSkill, unpublishSkill, updateSkill, deleteSkill, cloneSkill } from "@/services/api";
import { getUserId } from "@/services/auth";

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
    } catch {
      /* ignore */
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
        <textarea
          placeholder="Skill instructions in markdown..."
          value={instructions}
          onChange={(e) => setInstructions(e.target.value)}
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
