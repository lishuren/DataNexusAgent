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
  type: "file" | "text" | "textarea" | "select" | "url" | "number" | "toggle" | "onedrive-file" | "google-drive-file";
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

export interface DebugStep {
  step: string;
  status: string;
  preview?: string;
  chars?: number;
}

export interface ProcessingDebugInfo {
  inputPluginRan: boolean;
  parsedInputPreview: string;
  parsedInputLength: number;
  skillsUsed: string[];
  skillDetails: DebugStep[];
  systemPromptPreview: string;
  systemPromptLength: number;
  rawLlmResponse: string;
  outputPluginRan: boolean;
  outputPluginResult?: string;
}

export interface ProcessingResult {
  success: boolean;
  message: string;
  data?: unknown;
  warnings?: string[];
  debug?: ProcessingDebugInfo;
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

// --- Orchestrations ---

export type OrchestrationStatus =
  | "Draft"
  | "Approved"
  | "Rejected"
  | "Running"
  | "Completed"
  | "Failed";

export interface OrchestrationStep {
  stepNumber: number;
  title: string;
  description: string;
  agentId: number;
  agentName: string;
  isEdited: boolean;
  promptOverride: string | null;
  parameters: Record<string, string> | null;
}

export interface Orchestration {
  id: number;
  name: string;
  goal: string;
  steps: OrchestrationStep[];
  status: OrchestrationStatus;
  plannerModel: string | null;
  plannerNotes: string | null;
  enableSelfCorrection: boolean;
  maxCorrectionAttempts: number;
  scope: "Public" | "Private";
  ownerId: string | null;
  publishedByUserId: string | null;
  approvedAt: string | null;
  createdAt: string;
  updatedAt: string;
}
