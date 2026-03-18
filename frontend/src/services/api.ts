import { getToken } from "./auth";
import type { ProcessingRequest, ProcessingResult, Skill } from "@/types/api";

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

// --- Processing ---

export const processData = (request: ProcessingRequest) =>
  apiFetch<ProcessingResult>("/process", {
    method: "POST",
    body: JSON.stringify(request),
  });
