interface QuickAction {
  icon: string;
  title: string;
  desc: string;
}

interface QuickActionsProps {
  actions: QuickAction[];
  onAction?: (title: string) => void;
}

export function QuickActions({ actions, onAction }: QuickActionsProps) {
  if (actions.length === 0) return null;

  return (
    <div className="quick-actions-grid">
      {actions.map((qa) => (
        <div
          key={qa.title}
          className="card quick-action-card"
          onClick={() => onAction?.(qa.title)}
        >
          <div style={{ fontSize: "1.5rem", marginBottom: "0.25rem" }}>{qa.icon}</div>
          <div style={{ fontSize: "0.85rem", fontWeight: 600 }}>{qa.title}</div>
          <div style={{ fontSize: "0.75rem", color: "var(--text-muted)" }}>{qa.desc}</div>
        </div>
      ))}
    </div>
  );
}
