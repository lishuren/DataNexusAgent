import { getToken, logout } from "./auth";
import type {
  Agent,
  ConcurrentAggregatorMode,
  ExecutionMode,
  Orchestration,
  OrchestrationStep,
  Pipeline,
  PipelineRequest,
  ProcessingRequest,
  ProcessingResult,
  ProcessingStreamEvent,
  Skill,
  TaskHistory,
} from "@/types/api";

const BASE_URL = "/api";

async function apiFetch<T>(path: string, init?: RequestInit): Promise<T> {
  const token = getToken();
  const res = await fetch(`${BASE_URL}${path}`, {
    ...init,
    headers: {
      "Content-Type": "application/json",
      ...(token ? { Authorization: `Bearer ${token}` } : {}),
      ...init?.headers,
    },
  });

  // If the backend rejects the token (user removed from Keycloak, token expired),
  // force a logout so the user is redirected to the Keycloak login page.
  if (res.status === 401) {
    logout();
    throw new Error("Session expired — redirecting to login");
  }

  if (!res.ok) {
    const body = await res.text();
    throw new Error(`API ${res.status}: ${body}`);
  }

  return res.json() as Promise<T>;
}

async function apiStream(
  path: string,
  body: unknown,
  onEvent?: (event: ProcessingStreamEvent) => void,
): Promise<ProcessingResult> {
  const token = getToken();
  const res = await fetch(`${BASE_URL}${path}`, {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      ...(token ? { Authorization: `Bearer ${token}` } : {}),
    },
    body: JSON.stringify(body),
  });

  if (res.status === 401) {
    logout();
    throw new Error("Session expired — redirecting to login");
  }

  if (!res.ok) {
    const responseBody = await res.text();
    throw new Error(`API ${res.status}: ${responseBody}`);
  }

  if (!res.body) {
    throw new Error("Streaming response body is not available in this browser.");
  }

  const reader = res.body.getReader();
  const decoder = new TextDecoder();
  let buffer = "";
  let finalResult: ProcessingResult | null = null;

  const handleLine = (line: string) => {
    if (!line.trim()) return;

    const streamEvent = JSON.parse(line) as ProcessingStreamEvent;
    onEvent?.(streamEvent);

    if (streamEvent.type === "result" && streamEvent.result) {
      finalResult = streamEvent.result;
    }
  };

  while (true) {
    const { value, done } = await reader.read();
    if (done) break;

    buffer += decoder.decode(value, { stream: true });

    let newlineIndex = buffer.indexOf("\n");
    while (newlineIndex >= 0) {
      const line = buffer.slice(0, newlineIndex);
      buffer = buffer.slice(newlineIndex + 1);
      handleLine(line);
      newlineIndex = buffer.indexOf("\n");
    }
  }

  buffer += decoder.decode();
  if (buffer.trim()) {
    handleLine(buffer);
  }

  if (!finalResult) {
    throw new Error("Stream ended without a final result.");
  }

  return finalResult;
}

// --- Skills ---

export const listSkills = () => apiFetch<Skill[]>("/skills");

export const listPublicSkills = () => apiFetch<Skill[]>("/skills/public");

export const getSkill = (id: number) => apiFetch<Skill>(`/skills/${id}`);

export const createSkill = (name: string, instructions: string) =>
  apiFetch<{ id: number; name: string; scope: string }>("/skills", {
    method: "POST",
    body: JSON.stringify({ name, instructions }),
  });

export const updateSkill = (id: number, name: string, instructions: string) =>
  apiFetch<{ id: number; name: string; scope: string }>(`/skills/${id}`, {
    method: "PUT",
    body: JSON.stringify({ name, instructions }),
  });

export const deleteSkill = (id: number) =>
  apiFetch<void>(`/skills/${id}`, { method: "DELETE" });

export const cloneSkill = (id: number, name: string) =>
  apiFetch<{ id: number; name: string; scope: string }>(`/skills/${id}/clone`, {
    method: "POST",
    body: JSON.stringify({ name }),
  });

export const publishSkill = (id: number) =>
  apiFetch<{ name: string; scope: string }>(`/skills/${id}/publish`, {
    method: "POST",
  });

export const unpublishSkill = (id: number) =>
  apiFetch<{ id: number; name: string; scope: string }>(`/skills/${id}/unpublish`, {
    method: "POST",
  });

// --- Agents ---

export const listAgents = () => apiFetch<Agent[]>("/agents");

export const listPublicAgents = () => apiFetch<Agent[]>("/agents/public");

export const getAgent = (id: number) => apiFetch<Agent>(`/agents/${id}`);

export const createAgent = (agent: {
  name: string;
  icon?: string;
  description: string;
  systemPrompt?: string;
  uiSchema?: string;
  plugins?: string;
  skills?: string;
  executionType?: "Llm" | "External";
  command?: string;
  arguments?: string;
  workingDirectory?: string;
  timeoutSeconds?: number;
}) =>
  apiFetch<Agent>("/agents", {
    method: "POST",
    body: JSON.stringify(agent),
  });

export const updateAgent = (id: number, agent: {
  name: string;
  icon?: string;
  description: string;
  systemPrompt?: string;
  uiSchema?: string;
  plugins?: string;
  skills?: string;
  executionType?: "Llm" | "External";
  command?: string;
  arguments?: string;
  workingDirectory?: string;
  timeoutSeconds?: number;
}) =>
  apiFetch<Agent>(`/agents/${id}`, {
    method: "PUT",
    body: JSON.stringify(agent),
  });

export const deleteAgent = (id: number) =>
  apiFetch<void>(`/agents/${id}`, { method: "DELETE" });

export const cloneAgent = (id: number, name: string) =>
  apiFetch<Agent>(`/agents/${id}/clone`, {
    method: "POST",
    body: JSON.stringify({ name }),
  });

export const publishAgent = (id: number) =>
  apiFetch<Agent>(`/agents/${id}/publish`, {
    method: "POST",
  });

export const unpublishAgent = (id: number) =>
  apiFetch<Agent>(`/agents/${id}/unpublish`, {
    method: "POST",
  });

// --- Processing ---

export const processData = (request: ProcessingRequest) =>
  apiFetch<ProcessingResult>("/process", {
    method: "POST",
    body: JSON.stringify(request),
  });

export const processDataStream = (
  request: ProcessingRequest,
  onEvent?: (event: ProcessingStreamEvent) => void,
) => apiStream("/process/stream", request, onEvent);

export const runPipeline = (pipeline: PipelineRequest) =>
  apiFetch<ProcessingResult>("/process/pipeline", {
    method: "POST",
    body: JSON.stringify(pipeline),
  });

export const runPipelineStream = (
  pipeline: PipelineRequest,
  onEvent?: (event: ProcessingStreamEvent) => void,
) => apiStream("/process/pipeline/stream", pipeline, onEvent);

// --- Pipelines (CRUD) ---

export const listPipelines = () => apiFetch<Pipeline[]>("/pipelines");

export const listPublicPipelines = () => apiFetch<Pipeline[]>("/pipelines/public");

export const getPipeline = (id: number) => apiFetch<Pipeline>(`/pipelines/${id}`);

export const createPipeline = (pipeline: {
  name: string;
  agentIds: number[];
  enableSelfCorrection?: boolean;
  maxCorrectionAttempts?: number;
  executionMode?: ExecutionMode;
  concurrentAggregatorMode?: ConcurrentAggregatorMode;
}) =>
  apiFetch<Pipeline>("/pipelines", {
    method: "POST",
    body: JSON.stringify(pipeline),
  });

export const updatePipeline = (id: number, pipeline: {
  name: string;
  agentIds: number[];
  enableSelfCorrection?: boolean;
  maxCorrectionAttempts?: number;
  executionMode?: ExecutionMode;
  concurrentAggregatorMode?: ConcurrentAggregatorMode;
}) =>
  apiFetch<Pipeline>(`/pipelines/${id}`, {
    method: "PUT",
    body: JSON.stringify(pipeline),
  });

export const deletePipeline = (id: number) =>
  apiFetch<void>(`/pipelines/${id}`, { method: "DELETE" });

export const publishPipeline = (id: number) =>
  apiFetch<Pipeline>(`/pipelines/${id}/publish`, { method: "POST" });

export const unpublishPipeline = (id: number) =>
  apiFetch<Pipeline>(`/pipelines/${id}/unpublish`, { method: "POST" });

export const clonePipeline = (id: number, name: string) =>
  apiFetch<Pipeline>(`/pipelines/${id}/clone`, {
    method: "POST",
    body: JSON.stringify({ name }),
  });

// --- Task History ---

export const listTasks = () => apiFetch<TaskHistory[]>("/tasks");

// --- Current user (backend-confirmed identity) ---

export interface MeResponse {
  userId: string;
  displayName: string;
  email: string;
}

export const fetchMe = () => apiFetch<MeResponse>("/me");

// --- Orchestrations ---

export const planOrchestration = (request: {
  goal: string;
  name?: string;
  constraints?: string;
  agentIds?: number[];
  enableSelfCorrection?: boolean;
  maxCorrectionAttempts?: number;
  executionMode?: ExecutionMode;
  triageStepNumber?: number;
  groupChatMaxIterations?: number;
}) =>
  apiFetch<Orchestration>("/orchestrations/plan", {
    method: "POST",
    body: JSON.stringify(request),
  });

export const listOrchestrations = () =>
  apiFetch<Orchestration[]>("/orchestrations");

export const listPublicOrchestrations = () =>
  apiFetch<Orchestration[]>("/orchestrations/public");

export const getOrchestration = (id: number) =>
  apiFetch<Orchestration>(`/orchestrations/${id}`);

export const updateOrchestration = (id: number, data: {
  name: string;
  steps?: OrchestrationStep[];
  workflowKind?: "Structured" | "Graph";
  graph?: {
    nodes: Array<{
      id: string;
      displayOrder: number;
      title: string;
      description: string;
      agentId: number;
      agentName: string;
      isEdited: boolean;
      promptOverride: string | null;
      parameters: Record<string, string> | null;
      positionX: number;
      positionY: number;
    }>;
    edges: Array<{
      id: string;
      sourceNodeId: string;
      targetNodeId: string;
    }>;
  } | null;
  enableSelfCorrection?: boolean;
  maxCorrectionAttempts?: number;
  executionMode?: "Sequential" | "Concurrent" | "Handoff" | "GroupChat";
  triageStepNumber?: number;
  groupChatMaxIterations?: number;
}) =>
  apiFetch<Orchestration>(`/orchestrations/${id}`, {
    method: "PUT",
    body: JSON.stringify(data),
  });

export const approveOrchestration = (id: number) =>
  apiFetch<Orchestration>(`/orchestrations/${id}/approve`, { method: "POST" });

export const rejectOrchestration = (id: number, reason?: string) =>
  apiFetch<Orchestration>(`/orchestrations/${id}/reject`, {
    method: "POST",
    body: JSON.stringify({ reason }),
  });

export const resetOrchestration = (id: number) =>
  apiFetch<Orchestration>(`/orchestrations/${id}/reset`, { method: "POST" });

export const runOrchestration = (id: number, inputSource: string) =>
  apiFetch<ProcessingResult>(`/orchestrations/${id}/run`, {
    method: "POST",
    body: JSON.stringify({ inputSource }),
  });

export const runOrchestrationStream = (
  id: number,
  inputSource: string,
  onEvent?: (event: ProcessingStreamEvent) => void,
) => apiStream(`/orchestrations/${id}/run/stream`, { inputSource }, onEvent);

export const publishOrchestration = (id: number) =>
  apiFetch<Orchestration>(`/orchestrations/${id}/publish`, { method: "POST" });

export const unpublishOrchestration = (id: number) =>
  apiFetch<Orchestration>(`/orchestrations/${id}/unpublish`, { method: "POST" });

export const cloneOrchestration = (id: number, name: string) =>
  apiFetch<Orchestration>(`/orchestrations/${id}/clone`, {
    method: "POST",
    body: JSON.stringify({ name }),
  });

export const deleteOrchestration = (id: number) =>
  apiFetch<void>(`/orchestrations/${id}`, { method: "DELETE" });
