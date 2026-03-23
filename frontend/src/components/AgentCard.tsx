import type { Agent, Pipeline } from "@/types/api";

interface AgentCardProps {
  agent?: Agent;
  pipeline?: Pipeline;
  selected?: boolean;
  onClick?: () => void;
  agents?: Agent[];
}

const pluginDisplayName = (plugin: string) => plugin;

export function AgentCard({ agent, pipeline, selected, onClick, agents }: AgentCardProps) {
  if (pipeline) {
    const stepNames = pipeline.agentIds.map((id) => {
      const a = agents?.find((x) => x.id === id);
      return a ? `${a.icon} ${a.name}` : `Agent #${id}`;
    });

    return (
      <div className={`agent-card${selected ? " selected" : ""}`} onClick={onClick}>
        <div className="agent-icon">🔗</div>
        <div className="agent-name">{pipeline.name}</div>
        <div className="agent-desc">{stepNames.join(" → ")}</div>
        <div className="agent-meta">
          <span>{pipeline.agentIds.length} steps</span>
          {pipeline.enableSelfCorrection && <span>Self-correction</span>}
        </div>
      </div>
    );
  }

  if (!agent) return null;

  const isExternal = agent.executionType === "External";

  return (
    <div className={`agent-card${selected ? " selected" : ""}`} onClick={onClick}>
      <div className="agent-icon">{agent.icon}</div>
      <div className="agent-name">
        {agent.name}
        {isExternal && <span className="badge badge-external">External</span>}
      </div>
      <div className="agent-desc">{agent.description}</div>
      <div className="agent-meta">
        {(Array.isArray(agent.plugins) ? agent.plugins : (agent.plugins as unknown as string)?.split(","))?.filter(Boolean).map((p) => {
          const name = p.trim();
          return <span key={name}>{pluginDisplayName(name)}</span>;
        })}
        {isExternal && agent.command && <span>{agent.command}</span>}
        {agent.isBuiltIn
          ? <span>Built-in</span>
          : <span className={agent.scope === "Public" ? "badge-public" : ""}>{agent.scope}</span>
        }
      </div>
    </div>
  );
}
