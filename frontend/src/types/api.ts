export interface Skill {
  id: number;
  name: string;
  scope: "Public" | "Private";
  ownerId: string | null;
  publishedByUserId?: string | null;
  instructions?: string;
}

export interface Agent {
  id: number;
  name: string;
  icon: string;
  description: string;
  executionType: "Llm" | "External";
  systemPrompt: string;
  command?: string;
  arguments?: string;
  workingDirectory?: string;
  timeoutSeconds: number;
  uiSchema: UiField[] | string | null;
  plugins: string[] | string;
  skills: string[] | string;
  scope: "Public" | "Private";
  ownerId: string | null;
  publishedByUserId?: string | null;
  isBuiltIn: boolean;
}

export interface UiField {
  key: string;
  label: string;
  type: "file" | "text" | "textarea" | "select" | "url" | "number" | "toggle";
  placeholder?: string;
  required?: boolean;
  accept?: string;
  options?: string[];
  default?: string;
}

export interface ProcessingRequest {
  agentId?: number;
  inputSource: string;
  outputDestination: string;
  skillName?: string;
  parameters?: Record<string, string>;
}

export interface PipelineRequest {
  name: string;
  agentIds: number[];
  inputSource: string;
  outputDestination: string;
  enableSelfCorrection?: boolean;
  maxCorrectionAttempts?: number;
  parameters?: Record<string, string>;
}

export interface ProcessingResult {
  success: boolean;
  message: string;
  data?: unknown;
  warnings?: string[];
}

export interface Pipeline {
  id: number;
  name: string;
  agentIds: number[];
  enableSelfCorrection: boolean;
  maxCorrectionAttempts: number;
  scope: "Public" | "Private";
  ownerId: string | null;
  publishedByUserId?: string | null;
}

export interface TaskHistory {
  id: number;
  summary: string;
  agentId: number | null;
  agentName: string | null;
  pipelineId: number | null;
  pipelineName: string | null;
  success: boolean;
  message: string;
  rowCount: number | null;
  durationMs: number;
  createdAt: string;
}
