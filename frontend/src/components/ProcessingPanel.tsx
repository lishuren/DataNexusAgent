import { useState } from "react";
import type { ProcessingResult } from "@/types/api";
import { processData } from "@/services/api";

export function ProcessingPanel() {
  const [inputSource, setInputSource] = useState("");
  const [outputDestination, setOutputDestination] = useState("");
  const [skillName, setSkillName] = useState("");
  const [result, setResult] = useState<ProcessingResult | null>(null);
  const [loading, setLoading] = useState(false);

  const handleSubmit = async () => {
    if (!inputSource.trim() || !outputDestination.trim()) return;
    setLoading(true);
    setResult(null);

    try {
      const res = await processData({
        inputSource,
        outputDestination,
        skillName: skillName.trim() || undefined,
      });
      setResult(res);
    } catch (e) {
      setResult({
        success: false,
        message: e instanceof Error ? e.message : String(e),
      });
    } finally {
      setLoading(false);
    }
  };

  return (
    <>
      <div className="card">
        <h2>Process Data</h2>
        <input
          placeholder="Input source (URL or file path, e.g. https://example.com/data.xlsx)"
          value={inputSource}
          onChange={(e) => setInputSource(e.target.value)}
        />
        <input
          placeholder="Output destination (e.g. public-api)"
          value={outputDestination}
          onChange={(e) => setOutputDestination(e.target.value)}
        />
        <input
          placeholder="Skill name (optional, e.g. custom-parser)"
          value={skillName}
          onChange={(e) => setSkillName(e.target.value)}
        />
        <button className="btn btn-primary" onClick={handleSubmit} disabled={loading}>
          {loading ? "Processing…" : "Run"}
        </button>
      </div>

      {result && (
        <div className={`result-box ${result.success ? "result-success" : "result-error"}`}>
          <strong>{result.success ? "Success" : "Failed"}</strong>
          <br />
          {result.message}
          {result.warnings && result.warnings.length > 0 && (
            <>
              <br />
              <br />
              <strong>Warnings:</strong>
              <ul>
                {result.warnings.map((w, i) => (
                  <li key={i}>{w}</li>
                ))}
              </ul>
            </>
          )}
        </div>
      )}
    </>
  );
}
