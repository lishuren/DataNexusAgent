export interface Skill {
  name: string;
  scope: "Public" | "Private";
  ownerId: string | null;
  instructions?: string;
}

export interface ProcessingRequest {
  inputSource: string;
  outputDestination: string;
  skillName?: string;
  parameters?: Record<string, string>;
}

export interface ProcessingResult {
  success: boolean;
  message: string;
  data?: unknown;
  warnings?: string[];
}
