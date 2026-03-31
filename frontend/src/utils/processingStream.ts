import type { ProcessingStreamEvent } from "@/types/api";

export interface LiveRunState {
  active: boolean;
  statusLines: string[];
  transcript: string;
  lastSourceId: string | null;
}

export function createLiveRunState(initialStatus?: string): LiveRunState {
  return {
    active: true,
    statusLines: initialStatus ? [initialStatus] : [],
    transcript: "",
    lastSourceId: null,
  };
}

export function applyProcessingStreamEvent(state: LiveRunState, event: ProcessingStreamEvent): LiveRunState {
  if (event.type === "status") {
    if (!event.message) return state;
    return {
      ...state,
      statusLines: [...state.statusLines, event.message].slice(-6),
    };
  }

  if (event.type === "chunk") {
    if (!event.text) return state;

    const nextSourceId = event.sourceId ?? state.lastSourceId;
    const sourceChanged = Boolean(event.sourceId) && event.sourceId !== state.lastSourceId;
    const prefix = sourceChanged
      ? `${state.transcript ? "\n\n" : ""}[${event.sourceId}]\n`
      : "";

    return {
      ...state,
      transcript: `${state.transcript}${prefix}${event.text}`,
      lastSourceId: nextSourceId ?? null,
    };
  }

  if (event.type === "result") {
    const finalStatus = event.result?.success ? "Run completed." : "Run failed.";
    return {
      ...state,
      active: false,
      statusLines: finalStatus
        ? [...state.statusLines, finalStatus].slice(-6)
        : state.statusLines,
    };
  }

  return state;
}