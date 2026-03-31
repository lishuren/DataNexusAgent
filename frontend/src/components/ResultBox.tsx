import { useState } from "react";
import type { ProcessingResult } from "@/types/api";

interface ResultBoxProps {
  result: ProcessingResult;
}

function DebugSection({ label, meta, content }: { label: string; meta?: string; content: string }) {
  return (
    <div className="result-debug-section">
      <div className="result-debug-label">
        {label}
        {meta && <span className="result-debug-meta">{meta}</span>}
      </div>
      <pre className="result-data-pre">{content}</pre>
    </div>
  );
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
          <button className="result-debug-toggle" onClick={() => setDebugOpen((o) => !o)}>
            {debugOpen ? "▾" : "▸"} Debug info
            <span className="result-debug-pipeline">
              {d.inputPluginRan && <span className="debug-badge">InputProcessor</span>}
              {d.skillsUsed.map((s) => <span key={s} className="debug-badge debug-badge-skill">{s}</span>)}
              <span className="debug-badge debug-badge-llm">LLM</span>
              {d.outputPluginRan && <span className="debug-badge">OutputIntegrator</span>}
            </span>
          </button>

          {debugOpen && (
            <div className="result-debug-body">

              {/* Step 1 – InputProcessor */}
              {d.inputPluginRan ? (
                <DebugSection
                  label="① InputProcessor output (sent to LLM as user message)"
                  meta={`${d.parsedInputLength.toLocaleString()} chars`}
                  content={d.parsedInputPreview}
                />
              ) : (
                <div className="result-debug-note">① InputProcessor — not used for this agent</div>
              )}

              {/* Step 2 – Skills */}
              {d.skillDetails.length > 0 ? (
                d.skillDetails.map((sk) => (
                  <DebugSection
                    key={sk.step}
                    label={`② Skill Package: ${sk.step} (${sk.status})`}
                    meta={sk.chars ? `${sk.chars.toLocaleString()} chars` : undefined}
                    content={sk.preview ?? ""}
                  />
                ))
              ) : (
                <div className="result-debug-note">② Skill packages — none configured</div>
              )}

              {/* Step 3 – System prompt */}
              <DebugSection
                label="③ Agent instructions (skills are advertised through MAF context providers)"
                meta={`${d.systemPromptLength.toLocaleString()} chars`}
                content={d.systemPromptPreview}
              />

              {/* Step 4 – Raw LLM response */}
              <DebugSection
                label="④ Raw LLM response"
                meta={`${d.rawLlmResponse.length.toLocaleString()} chars`}
                content={d.rawLlmResponse || "(empty)"}
              />

              {/* Step 5 – OutputIntegrator */}
              {d.outputPluginRan ? (
                <DebugSection
                  label="⑤ OutputIntegrator result"
                  content={d.outputPluginResult ?? "(no output)"}
                />
              ) : (
                <div className="result-debug-note">⑤ OutputIntegrator — not used for this agent</div>
              )}

            </div>
          )}
        </div>
      )}
    </div>
  );
}
