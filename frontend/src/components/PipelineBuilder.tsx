import { useState, useRef, useEffect } from "react";
import type { Agent } from "@/types/api";

interface PipelineBuilderProps {
  steps: number[];
  agents: Agent[];
  onChange: (steps: number[]) => void;
}

export function PipelineBuilder({ steps, agents, onChange }: PipelineBuilderProps) {
  const [pickerOpen, setPickerOpen] = useState(false);
  const pickerRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    const handler = (e: MouseEvent) => {
      if (pickerRef.current && !pickerRef.current.contains(e.target as Node)) {
        setPickerOpen(false);
      }
    };
    document.addEventListener("click", handler);
    return () => document.removeEventListener("click", handler);
  }, []);

  const getAgent = (id: number) => agents.find((a) => a.id === id);

  return (
    <div className="pipeline">
      {steps.map((stepId, idx) => {
        const agent = getAgent(stepId);
        if (!agent) return null;
        const isExternal = agent.executionType === "External";
        return (
          <span key={`${stepId}-${idx}`} style={{ display: "contents" }}>
            <div className="pipeline-node">
              {agent.icon} {agent.name}
              {isExternal && <span style={{ fontSize: "0.6rem", color: "#f59e0b" }}>EXT</span>}
              <button
                className="pipeline-remove"
                onClick={() => onChange(steps.filter((_, i) => i !== idx))}
                title="Remove"
              >
                ✕
              </button>
            </div>
            <span className="pipeline-arrow">→</span>
          </span>
        );
      })}

      <div ref={pickerRef} style={{ position: "relative", display: "inline-block" }}>
        <button className="pipeline-add" onClick={() => setPickerOpen(!pickerOpen)}>
          + Add step
        </button>
        {pickerOpen && (
          <div className="agent-picker">
            {agents.map((a) => (
              <div
                key={a.id}
                className="agent-picker-item"
                onClick={() => {
                  onChange([...steps, a.id]);
                  setPickerOpen(false);
                }}
              >
                {a.icon} {a.name}
                {a.executionType === "External" && (
                  <span style={{ fontSize: "0.65rem", color: "#f59e0b" }}> (External)</span>
                )}
              </div>
            ))}
          </div>
        )}
      </div>
    </div>
  );
}
