import { useCallback, useEffect, useState } from "react";
import { listOrchestrations, listPublicOrchestrations } from "@/services/api";
import type { Orchestration } from "@/types/api";

export function useOrchestrations() {
  const [orchestrations, setOrchestrations] = useState<Orchestration[]>([]);
  const [loading, setLoading] = useState(true);

  const refresh = useCallback(async () => {
    setLoading(true);
    try {
      setOrchestrations(await listOrchestrations());
    } catch (e) {
      console.warn("useOrchestrations: fetch failed", e);
    }
    setLoading(false);
  }, []);

  useEffect(() => { refresh(); }, [refresh]);

  return { orchestrations, loading, refresh };
}

export function usePublicOrchestrations() {
  const [orchestrations, setOrchestrations] = useState<Orchestration[]>([]);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    listPublicOrchestrations()
      .then(setOrchestrations)
      .catch((e) => console.warn("usePublicOrchestrations: fetch failed", e))
      .finally(() => setLoading(false));
  }, []);

  return { orchestrations, loading };
}
