import { getToken } from "./auth";
import type { Agent, Pipeline, PipelineRequest, ProcessingRequest, ProcessingResult, Skill } from "@/types/api";

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

  if (!res.ok) {
    const body = await res.text();
    throw new Error(`API ${res.status}: ${body}`);
  }

  return res.json() as Promise<T>;
}

// --- Skills ---

export const listSkills = () => apiFetch<Skill[]>("/skills");

export const listPublicSkills = () => apiFetch<Skill[]>("/skills/public");

export const createSkill = (name: string, instructions: string) =>
  apiFetch<{ name: string; scope: string }>("/skills", {
    method: "POST",
    body: JSON.stringify({ name, instructions }),
  });

export const publishSkill = (name: string) =>
  apiFetch<{ name: string; scope: string }>(`/skills/${encodeURIComponent(name)}/publish`, {
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

export const publishAgent = (id: number) =>
  apiFetch<Agent>(`/agents/${id}/publish`, {
    method: "POST",
  });

// --- Processing ---

export const processData = (request: ProcessingRequest) =>
  apiFetch<ProcessingResult>("/process", {
    method: "POST",
    body: JSON.stringify(request),
  });

export const runPipeline = (pipeline: PipelineRequest) =>
  apiFetch<ProcessingResult>("/process/pipeline", {
    method: "POST",
    body: JSON.stringify(pipeline),
  });

// --- Pipelines (CRUD) ---

export const listPipelines = () => apiFetch<Pipeline[]>("/pipelines");

export const listPublicPipelines = () => apiFetch<Pipeline[]>("/pipelines/public");

export const getPipeline = (id: number) => apiFetch<Pipeline>(`/pipelines/${id}`);

export const createPipeline = (pipeline: {
  name: string;
  agentIds: number[];
  enableSelfCorrection?: boolean;
  maxCorrectionAttempts?: number;
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
}) =>
  apiFetch<Pipeline>(`/pipelines/${id}`, {
    method: "PUT",
    body: JSON.stringify(pipeline),
  });

export const deletePipeline = (id: number) =>
  apiFetch<void>(`/pipelines/${id}`, { method: "DELETE" });

export const publishPipeline = (id: number) =>
  apiFetch<Pipeline>(`/pipelines/${id}/publish`, { method: "POST" });
