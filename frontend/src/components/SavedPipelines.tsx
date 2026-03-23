import type { Agent, Pipeline } from "@/types/api";

interface SavedPipelinesProps {
  pipelines: Pipeline[];
  agents: Agent[];
  onEdit: (pipeline: Pipeline) => void;
  onDelete: (id: number) => void;
  onClone: (pipeline: Pipeline) => void;
  onPublish: (id: number) => void;
  onUnpublish: (id: number) => void;
  currentUserId?: string;
}

export function SavedPipelines({
  pipelines,
  agents,
  onEdit,
  onDelete,
  onClone,
  onPublish,
  onUnpublish,
  currentUserId,
}: SavedPipelinesProps) {
  if (pipelines.length === 0) {
    return (
      <p style={{ color: "var(--text-muted)", fontSize: "0.85rem", padding: "0.75rem 0" }}>
        No saved pipelines yet. Compose one above.
      </p>
    );
  }

  const getAgent = (id: number) => agents.find((a) => a.id === id);

  return (
    <ul className="skill-list">
      {pipelines.map((p) => {
        const stepNames = p.agentIds
          .map((id) => {
            const a = getAgent(id);
            return a ? `${a.icon} ${a.name}` : `#${id}`;
          })
          .join(" → ");

        return (
          <li key={p.id}>
            <span style={{ display: "flex", flexDirection: "column", gap: "2px" }}>
              <span style={{ fontWeight: 600, fontSize: "0.85rem" }}>
                🔗 {p.name}
                {p.enableSelfCorrection && (
                  <span className="badge badge-public" style={{ marginLeft: "6px" }}>Self-correction</span>
                )}
              </span>
              <span style={{ fontSize: "0.75rem", color: "var(--text-muted)" }}>{stepNames}</span>
            </span>
            <span style={{ display: "flex", gap: "0.5rem" }}>
              {p.ownerId === currentUserId && p.scope === "Private" && (
                <button className="btn btn-sm btn-primary" onClick={() => onPublish(p.id)}>Publish</button>
              )}
              {p.publishedByUserId === currentUserId && p.scope === "Public" && (
                <button className="btn btn-sm btn-outline" onClick={() => onUnpublish(p.id)}>Unpublish</button>
              )}
              {p.ownerId === currentUserId && p.scope === "Private" && (
                <button className="btn btn-sm btn-primary" onClick={() => onEdit(p)}>Edit</button>
              )}
              {p.ownerId === currentUserId && p.scope === "Private" && (
                <button
                  className="btn btn-sm btn-outline btn-outline-danger"
                  onClick={() => onDelete(p.id)}
                >
                  Delete
                </button>
              )}
              <button className="btn btn-sm btn-outline" onClick={() => onClone(p)}>Clone</button>
            </span>
          </li>
        );
      })}
    </ul>
  );
}
