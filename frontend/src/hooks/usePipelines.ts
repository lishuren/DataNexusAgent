import { useCallback, useEffect, useState } from "react";
import { listPipelines, listPublicPipelines } from "@/services/api";
import type { Pipeline } from "@/types/api";

export function usePipelines() {
  const [pipelines, setPipelines] = useState<Pipeline[]>([]);
  const [loading, setLoading] = useState(true);

  const refresh = useCallback(async () => {
    setLoading(true);
    try {
      setPipelines(await listPipelines());
    } catch (e) { console.warn("usePipelines: fetch failed", e); }
    setLoading(false);
  }, []);

  useEffect(() => { refresh(); }, [refresh]);

  return { pipelines, loading, refresh };
}

export function usePublicPipelines() {
  const [pipelines, setPipelines] = useState<Pipeline[]>([]);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    listPublicPipelines().then(setPipelines).catch((e) => console.warn("usePublicPipelines: fetch failed", e)).finally(() => setLoading(false));
  }, []);

  return { pipelines, loading };
}
