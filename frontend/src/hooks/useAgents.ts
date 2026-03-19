import { useCallback, useEffect, useState } from "react";
import { listAgents, listPublicAgents } from "@/services/api";
import type { Agent } from "@/types/api";

export function useAgents() {
  const [agents, setAgents] = useState<Agent[]>([]);
  const [loading, setLoading] = useState(true);

  const refresh = useCallback(async () => {
    setLoading(true);
    try {
      setAgents(await listAgents());
    } catch { /* auth may not be ready */ }
    setLoading(false);
  }, []);

  useEffect(() => { refresh(); }, [refresh]);

  return { agents, loading, refresh };
}

export function usePublicAgents() {
  const [agents, setAgents] = useState<Agent[]>([]);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    listPublicAgents().then(setAgents).catch(() => {}).finally(() => setLoading(false));
  }, []);

  return { agents, loading };
}
