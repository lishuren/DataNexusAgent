import type { ProcessingResult } from "@/types/api";

interface ResultBoxProps {
  result: ProcessingResult;
}

export function ResultBox({ result }: ResultBoxProps) {
  return (
    <div className={`result-box ${result.success ? "result-success" : "result-error"}`}>
      <strong>{result.success ? "✓ Task Completed" : "✗ Task Failed"}</strong>
      {"\n"}
      {result.message}
      {result.warnings && result.warnings.length > 0 && (
        <>
          {"\n\n"}
          <strong>Warnings:</strong>
          {result.warnings.map((w, i) => (
            <span key={i}>{"\n"}• {w}</span>
          ))}
        </>
      )}
    </div>
  );
}
