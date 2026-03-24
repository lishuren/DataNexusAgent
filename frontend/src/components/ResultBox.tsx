import { useState } from "react";
import type { ProcessingResult } from "@/types/api";

interface ResultBoxProps {
  result: ProcessingResult;
}

export function ResultBox({ result }: ResultBoxProps) {
  const [copied, setCopied] = useState(false);
  const [debugOpen, setDebugOpen] = useState(false);

  const dataStr = result.data != null
    ? (typeof result.data === "string" ? result.data : JSON.stringify(result.data, null, 2))
    : null;

  const handleCopy = () => {
    if (!dataStr) return;
    navigator.clipboard.writeText(dataStr).then(() => {
      setCopied(true);
      setTimeout(() => setCopied(false), 2000);
    });
  };

  const d = result.debug;

  return (
    <div className={`result-box ${result.success ? "result-success" : "result-error"}`}>
      <strong>{result.success ? "✓ Task Completed" : "✗ Task Failed"}</strong>
      {"\n"}
      {result.message}

      {dataStr && (
        <div className="result-data">
          <div className="result-data-header">
            <span>Result</span>
            <button className="btn-copy" onClick={handleCopy}>
              {copied ? "✓ Copied" : "Copy"}
            </button>
          </div>
          <pre className="result-data-pre">{dataStr}</pre>
        </div>
      )}

      {result.warnings && result.warnings.length > 0 && (
        <>
          {"\n\n"}
          <strong>Warnings:</strong>
          {result.warnings.map((w, i) => (
            <span key={i}>{"\n"}• {w}</span>
          ))}
        </>
      )}

      {d && (
        <div className="result-debug">
          <button
            className="result-debug-toggle"
            onClick={() => setDebugOpen((o) => !o)}
          >
            {debugOpen ? "▾" : "▸"} Debug info
            {d.skillsUsed.length > 0 && (
              <span className="result-debug-skills">
                {d.skillsUsed.map((s) => (
                  <span key={s} className="badge badge-public" style={{ marginLeft: 4 }}>{s}</span>
                ))}
              </span>
            )}
          </button>

          {debugOpen && (
            <div className="result-debug-body">
              <div className="result-debug-section">
                <div className="result-debug-label">
                  Parsed input sent to LLM
                  <span className="result-debug-meta">{d.parsedInputLength.toLocaleString()} chars</span>
                </div>
                <pre className="result-data-pre">{d.parsedInputPreview}</pre>
              </div>

              {d.rawLlmResponse && d.rawLlmResponse !== dataStr && (
                <div className="result-debug-section">
                  <div className="result-debug-label">Raw LLM response</div>
                  <pre className="result-data-pre">{d.rawLlmResponse}</pre>
                </div>
              )}
            </div>
          )}
        </div>
      )}
    </div>
  );
}
