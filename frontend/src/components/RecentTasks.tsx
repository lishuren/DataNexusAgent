export function RecentTasks() {
  // Placeholder — will be wired to real data when backend supports task history
  const tasks = [
    { success: true, label: "Parse Q1-2026.xlsx → API upload", badge: "1.2k rows", time: "2 min ago" },
    { success: true, label: "Clean addresses in customer-data.csv", badge: "843 rows", time: "1 hour ago" },
    { success: false, label: "Transform inventory.json → DB write", badge: "Schema error", time: "3 hours ago" },
    { success: true, label: "Normalize dates in legacy-export.csv", badge: "5.6k rows", time: "Yesterday" },
  ];

  return (
    <div className="card" style={{ marginTop: "1rem" }}>
      <h2>🕘 Recent Tasks</h2>
      <ul className="skill-list">
        {tasks.map((t, i) => (
          <li key={i}>
            <span>
              <span style={{ color: t.success ? "var(--success)" : "var(--danger)", fontWeight: 600 }}>
                {t.success ? "✓" : "✗"}
              </span>{" "}
              {t.label}
              <span
                className="badge"
                style={{
                  marginLeft: 8,
                  background: t.success ? "rgba(34,197,94,0.15)" : "rgba(239,68,68,0.15)",
                  color: t.success ? "var(--success)" : "var(--danger)",
                }}
              >
                {t.badge}
              </span>
            </span>
            <span style={{ color: "var(--text-muted)", fontSize: "0.75rem" }}>{t.time}</span>
          </li>
        ))}
      </ul>
    </div>
  );
}
