import { useCallback, useEffect, useState } from "react";
import { listSkills, listPublicSkills } from "@/services/api";
import type { Skill } from "@/types/api";

export function useSkills() {
  const [skills, setSkills] = useState<Skill[]>([]);
  const [loading, setLoading] = useState(true);

  const refresh = useCallback(async () => {
    setLoading(true);
    try {
      setSkills(await listSkills());
    } catch (e) { console.warn("useSkills: fetch failed", e); }
    setLoading(false);
  }, []);

  useEffect(() => { refresh(); }, [refresh]);

  return { skills, loading, refresh };
}

export function usePublicSkills() {
  const [skills, setSkills] = useState<Skill[]>([]);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    listPublicSkills().then(setSkills).catch((e) => console.warn("usePublicSkills: fetch failed", e)).finally(() => setLoading(false));
  }, []);

  return { skills, loading };
}
