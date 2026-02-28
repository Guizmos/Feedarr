import { useState, useEffect, useRef, useCallback } from "react";
import { apiGet, apiPost } from "../api/client.js";
import { addTask, removeTask } from "../app/taskTracker.js";

const STORAGE_KEY = "feedarr:retroFetch";
const TASK_KEY = "retro-fetch-posters";

function loadRetroTask() {
  if (typeof window === "undefined") return null;
  try {
    const raw = window.localStorage.getItem(STORAGE_KEY);
    if (!raw) return null;
    const parsed = JSON.parse(raw);
    if (!parsed || !parsed.active) return null;
    // Expire task after 1 hour
    if (parsed.startedAt && Date.now() - parsed.startedAt > 1000 * 60 * 60 * 1) {
      window.localStorage.removeItem(STORAGE_KEY);
      return null;
    }
    return parsed;
  } catch {
    return null;
  }
}

function saveRetroTask(task) {
  if (typeof window === "undefined") return;
  if (!task) {
    window.localStorage.removeItem(STORAGE_KEY);
    // Supprimer aussi la tâche du taskTracker
    removeTask(TASK_KEY);
  } else {
    try {
      window.localStorage.setItem(STORAGE_KEY, JSON.stringify(task));
    } catch {
      // ignore
    }
    // Synchroniser avec le taskTracker (addTask écrase automatiquement si existe)
    if (task.active) {
      const total = Number(task.total ?? 0);
      const done = Number(task.done ?? 0);
      const percent = Number(task.percent ?? 0);
      const meta = total > 0
        ? `${percent}% (${done}/${total})`
        : "En cours...";

      addTask({
        key: TASK_KEY,
        label: "Récupération de posters",
        meta,
        ttlMs: 1000 * 60 * 60, // 1 heure max
      });
    }
  }
  // Dispatch a custom event to notify other components
  window.dispatchEvent(new CustomEvent("storage_change", { detail: { key: STORAGE_KEY } }));
}

export function useRetroFetchProgress() {
  const [retroTask, setRetroTask] = useState(() => loadRetroTask());
  const pollRef = useRef(null);
  const isPollingRef = useRef(false);

  // Synchroniser la tâche chargée avec taskTracker au montage
  useEffect(() => {
    const initialTask = loadRetroTask();
    if (initialTask?.active) {
      const total = Number(initialTask.total ?? 0);
      const done = Number(initialTask.done ?? 0);
      const percent = Number(initialTask.percent ?? 0);
      const meta = total > 0
        ? `${percent}% (${done}/${total})`
        : "En cours...";

      addTask({
        key: TASK_KEY,
        label: "Récupération de posters",
        meta,
        ttlMs: 1000 * 60 * 60,
      });
    }
  }, []); // Exécute uniquement au montage

  const stopPolling = useCallback(() => {
    if (pollRef.current) {
      clearInterval(pollRef.current);
      pollRef.current = null;
    }
    isPollingRef.current = false;
  }, []);

  const handleStorageChange = useCallback((event) => {
    if (event.detail.key === STORAGE_KEY) {
      const task = loadRetroTask();
      setRetroTask(task);
      if (!task?.active) {
        stopPolling();
      }
    }
  }, [stopPolling]);

  useEffect(() => {
    window.addEventListener("storage_change", handleStorageChange);
    return () => {
      window.removeEventListener("storage_change", handleStorageChange);
    };
  }, [handleStorageChange]);

  useEffect(() => {
    if (!retroTask?.active) {
      stopPolling();
      return;
    }

    if (isPollingRef.current) return;
    isPollingRef.current = true;

    async function tick() {
      try {
        const ids = Array.isArray(retroTask.ids) ? retroTask.ids : [];
        const startedAtTs = Number(retroTask.startedAtTs ?? 0);
        
        let updatedTask = { ...retroTask };

        if (ids.length > 0) {
          const res = await apiPost("/api/posters/retro-fetch/progress", { ids, startedAtTs });
          const total = Number(res?.total ?? ids.length);
          const done = Number(res?.done ?? 0);
          
          if (total > 0 && done >= total) {
            saveRetroTask(null);
            return;
          }

          updatedTask = { ...updatedTask, done, total, percent: total > 0 ? Math.round((done / total) * 100) : 0 };
        } else {
            const mc = await apiGet("/api/posters/missing-count");
            const currentMissing = Number(mc?.count ?? 0);
            const startMissing = Number(retroTask.startMissing ?? 0);
            const targetMissing = Number(retroTask.targetMissing ?? 0);
            const denom = Math.max(1, startMissing - targetMissing);
            const done = Math.max(0, startMissing - currentMissing);

            if (currentMissing <= targetMissing || denom === 0) {
                saveRetroTask(null);
                return;
            }
            updatedTask = { ...updatedTask, currentMissing, percent: Math.round((done / denom) * 100) };
        }
        
        saveRetroTask(updatedTask);

      } catch {
        // Silently ignore for now, but could add error handling
      }
    }

    tick();
    pollRef.current = setInterval(tick, 4000);

    return () => {
      stopPolling();
    };
  }, [retroTask, stopPolling]);

  const startRetroFetch = useCallback(async () => {
    try {
        const missingRes = await apiGet("/api/posters/missing-count");
        const startMissing = Number(missingRes?.count ?? 0);

        const res = await apiPost("/api/posters/retro-fetch", { limit: 300 });
        const enqueued = Number(res?.enqueued ?? 0);
        const ids = Array.isArray(res?.ids) ? res.ids : [];
        const startedAtTs = Number(res?.startedAtTs ?? Math.floor(Date.now() / 1000));

        const task = {
            active: true,
            startedAt: Date.now(),
            startMissing,
            targetMissing: Math.max(0, startMissing - enqueued),
            enqueued,
            total: Number(res?.total ?? enqueued),
            ids,
            startedAtTs,
            currentMissing: startMissing,
            percent: 0,
            done: 0,
        };

        // saveRetroTask va synchroniser avec taskTracker automatiquement
        saveRetroTask(task);
        return { task, error: null };
    } catch (e) {
        return { task: null, error: e.message || "Failed to start retro fetch" };
    }
  }, []);

  const stopRetroFetch = useCallback(async () => {
    try {
        await apiPost("/api/posters/retro-fetch/stop", {});
        saveRetroTask(null); // Ceci va aussi appeler removeTask(TASK_KEY)
        return { error: null };
    } catch(e) {
        return { error: e.message || "Failed to stop retro fetch" };
    }
  }, []);

  return { retroTask, startRetroFetch, stopRetroFetch };
}
