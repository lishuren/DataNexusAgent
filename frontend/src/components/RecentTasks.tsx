import { useCallback, useEffect, useState } from "react";
import type { TaskHistory } from "@/types/api";
import { listTasks } from "@/services/api";

export function RecentTasks() {
  const [tasks, setTasks] = useState<TaskHistory[]>([]);

  const refresh = useCallback(async () => {
    try {
      setTasks(await listTasks());
    } catch (e) {
      console.warn("Failed to load tasks:", e);
    }
  }, []);

  useEffect(() => { refresh(); }, [refresh]);

  if (tasks.length === 0) return null;

  const formatTime = (iso: string) => {
    const diff = Date.now() - new Date(iso).getTime();
    const mins = Math.floor(diff / 60_000);
    if (mins < 1) return "Just now";
    if (mins < 60) return `${mins} min ago`;
    const hours = Math.floor(mins / 60);
    if (hours < 24) return `${hours} hour${hours > 1 ? "s" : ""} ago`;
    const days = Math.floor(hours / 24);
    return days === 1 ? "Yesterday" : `${days} days ago`;
  };

  const formatDuration = (ms: number) =>
    ms < 1000 ? `${Math.round(ms)}ms` : `${(ms / 1000).toFixed(1)}s`;

  return (
    <div className="card" style={{ marginTop: "1rem" }}>
      <h2>🕘 Recent Tasks</h2>
      <ul className="skill-list">
        {tasks.map((t) => (
          <li key={t.id}>
            <span>
              <span style={{ color: t.success ? "var(--success)" : "var(--danger)", fontWeight: 600 }}>
                {t.success ? "✓" : "✗"}
              </span>{" "}
              {t.summary}
              {t.agentName && (
                <span className="badge badge-private" style={{ marginLeft: 8 }}>{t.agentName}</span>
              )}
              {t.pipelineName && (
                <span className="badge badge-public" style={{ marginLeft: 8 }}>{t.pipelineName}</span>
              )}
              <span style={{ marginLeft: 8, fontSize: "0.7rem", color: "var(--text-muted)" }}>
                {formatDuration(t.durationMs)}
              </span>
            </span>
            <span style={{ color: "var(--text-muted)", fontSize: "0.75rem" }}>{formatTime(t.createdAt)}</span>
          </li>
        ))}
      </ul>
    </div>
  );
}
