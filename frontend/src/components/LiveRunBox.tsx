import type { LiveRunState } from "@/utils/processingStream";

interface LiveRunBoxProps {
  state: LiveRunState;
  title?: string;
}

export function LiveRunBox({ state, title = "Live Run Stream" }: LiveRunBoxProps) {
  if (!state.transcript && state.statusLines.length === 0) {
    return null;
  }

  return (
    <div className="live-run-box">
      <div className="live-run-header">
        <strong>{title}</strong>
        <span className={`live-run-badge ${state.active ? "live-run-badge-active" : "live-run-badge-complete"}`}>
          {state.active ? "Streaming" : "Captured"}
        </span>
      </div>

      {state.statusLines.length > 0 && (
        <div className="live-run-status-list">
          {state.statusLines.map((line, index) => (
            <div key={`${index}-${line}`} className="live-run-status-line">{line}</div>
          ))}
        </div>
      )}

      {state.transcript && (
        <pre className="live-run-transcript">{state.transcript}</pre>
      )}
    </div>
  );
}