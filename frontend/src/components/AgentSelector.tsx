import type { Agent, Pipeline } from "@/types/api";
import { AgentCard } from "./AgentCard";

interface AgentSelectorProps {
  agents: Agent[];
  pipelines: Pipeline[];
  selectedAgentId?: number;
  selectedPipelineId?: number;
  onSelectAgent: (id: number) => void;
  onSelectPipeline: (id: number) => void;
}

export function AgentSelector({
  agents,
  pipelines,
  selectedAgentId,
  selectedPipelineId,
  onSelectAgent,
  onSelectPipeline,
}: AgentSelectorProps) {
  return (
    <div className="card">
      <h2>🤖 Select Agent</h2>
      <div className="agent-grid" style={{ marginBottom: "1rem" }}>
        {agents.map((a) => (
          <AgentCard
            key={a.id}
            agent={a}
            selected={selectedAgentId === a.id && !selectedPipelineId}
            onClick={() => onSelectAgent(a.id)}
          />
        ))}
      </div>

      {pipelines.length > 0 && (
        <>
          <h3
            style={{
              fontSize: "0.85rem",
              color: "var(--text-muted)",
              marginTop: "1rem",
              marginBottom: "0.5rem",
            }}
          >
            🔗 Saved Pipelines
          </h3>
          <div className="agent-grid">
            {pipelines.map((p) => (
              <AgentCard
                key={`p-${p.id}`}
                pipeline={p}
                agents={agents}
                selected={selectedPipelineId === p.id}
                onClick={() => onSelectPipeline(p.id)}
              />
            ))}
          </div>
        </>
      )}
    </div>
  );
}
