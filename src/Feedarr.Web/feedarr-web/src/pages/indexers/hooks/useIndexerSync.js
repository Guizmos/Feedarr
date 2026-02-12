import { useCallback, useMemo, useState } from "react";
import { apiPost } from "../../../api/client.js";
import { startRssSyncSingle, completeRssSyncSingle } from "../../../tasks/syncTasks.js";
import { startIndexerTest, completeIndexerTest } from "../../../tasks/indexerTasks.js";
import { triggerPosterPolling } from "../../../hooks/usePosterPollingService.js";

function clearStatusAfterDelay(setStatusById, id, delayMs = 1600) {
  setTimeout(() => {
    setStatusById((prev) => {
      const next = { ...prev };
      delete next[id];
      return next;
    });
  }, delayMs);
}

export default function useIndexerSync({ items, load, setErr }) {
  const [syncingId, setSyncingId] = useState(null);
  const [syncStatusById, setSyncStatusById] = useState({});
  const [syncAllRunning, setSyncAllRunning] = useState(false);
  const [testingId, setTestingId] = useState(null);
  const [testStatusById, setTestStatusById] = useState({});

  const hasPendingSync = useMemo(
    () => Object.values(syncStatusById || {}).some((value) => value === "pending"),
    [syncStatusById]
  );

  const syncAll = useCallback(async () => {
    const enabledItems = (items || []).filter((source) => source?.id && source?.enabled);
    if (enabledItems.length === 0) return;

    setErr("");
    const startedAt = Date.now();
    setSyncAllRunning(true);
    setSyncStatusById((prev) => {
      const next = { ...(prev || {}) };
      enabledItems.forEach((source) => {
        next[source.id] = "pending";
      });
      return next;
    });

    try {
      const results = await Promise.allSettled(
        enabledItems.map((source) => apiPost(`/api/sources/${source.id}/sync`))
      );
      await load();
      triggerPosterPolling("sync-all");
      const elapsed = Date.now() - startedAt;
      const wait = Math.max(2000 - elapsed, 0);

      setTimeout(() => {
        setSyncStatusById((prev) => {
          const next = { ...(prev || {}) };
          enabledItems.forEach((source, index) => {
            const result = results[index];
            const ok = result.status === "fulfilled" ? !!result.value?.ok : false;
            next[source.id] = ok ? "ok" : "error";
          });
          return next;
        });

        setTimeout(() => {
          setSyncStatusById((prev) => {
            const next = { ...(prev || {}) };
            enabledItems.forEach((source) => {
              delete next[source.id];
            });
            return next;
          });
        }, 1600);

        setSyncAllRunning(false);
      }, wait);
    } catch (error) {
      setErr(error?.message || "Erreur sync");
      const elapsed = Date.now() - startedAt;
      const wait = Math.max(2000 - elapsed, 0);

      setTimeout(() => {
        setSyncStatusById((prev) => {
          const next = { ...(prev || {}) };
          enabledItems.forEach((source) => {
            next[source.id] = "error";
          });
          return next;
        });

        setTimeout(() => {
          setSyncStatusById((prev) => {
            const next = { ...(prev || {}) };
            enabledItems.forEach((source) => {
              delete next[source.id];
            });
            return next;
          });
        }, 1600);

        setSyncAllRunning(false);
      }, wait);
    }
  }, [items, load, setErr]);

  const syncSource = useCallback(async (source) => {
    if (!source?.id || !source.enabled) return;
    if (testingId === source.id || syncAllRunning || syncStatusById[source.id] === "pending") return;

    setErr("");
    const startedAt = Date.now();
    setSyncingId(source.id);
    setSyncStatusById((prev) => ({ ...prev, [source.id]: "pending" }));
    startRssSyncSingle(source.id, source.name || `Source #${source.id}`);

    try {
      const result = await apiPost(`/api/sources/${source.id}/sync`);
      await load();
      triggerPosterPolling("sync");
      const elapsed = Date.now() - startedAt;
      const wait = Math.max(2000 - elapsed, 0);

      setTimeout(() => {
        setSyncStatusById((prev) => ({ ...prev, [source.id]: result?.ok ? "ok" : "error" }));
        clearStatusAfterDelay(setSyncStatusById, source.id);
        setSyncingId(null);
        completeRssSyncSingle(source.id);
      }, wait);
    } catch (error) {
      setErr(error?.message || "Erreur sync");
      const elapsed = Date.now() - startedAt;
      const wait = Math.max(2000 - elapsed, 0);

      setTimeout(() => {
        setSyncStatusById((prev) => ({ ...prev, [source.id]: "error" }));
        clearStatusAfterDelay(setSyncStatusById, source.id);
        setSyncingId(null);
        completeRssSyncSingle(source.id);
      }, wait);
    }
  }, [load, setErr, syncAllRunning, syncStatusById, testingId]);

  const testSource = useCallback(async (source) => {
    if (!source?.id || !source.enabled) return;
    if (syncingId === source.id || syncStatusById[source.id] === "pending") return;

    setErr("");
    const startedAt = Date.now();
    setTestingId(source.id);
    setTestStatusById((prev) => ({ ...prev, [source.id]: "pending" }));
    startIndexerTest(source.id, source.name || `Source #${source.id}`);

    try {
      const result = await apiPost(`/api/sources/${source.id}/test`, { rssLimit: 50 });
      const elapsed = Date.now() - startedAt;
      const wait = Math.max(2000 - elapsed, 0);

      setTimeout(() => {
        setTestStatusById((prev) => ({ ...prev, [source.id]: result?.ok ? "ok" : "error" }));
        clearStatusAfterDelay(setTestStatusById, source.id);
        setTestingId(null);
        completeIndexerTest(source.id);
      }, wait);
    } catch (error) {
      setErr(error?.message || "Erreur test");
      const elapsed = Date.now() - startedAt;
      const wait = Math.max(2000 - elapsed, 0);

      setTimeout(() => {
        setTestStatusById((prev) => ({ ...prev, [source.id]: "error" }));
        clearStatusAfterDelay(setTestStatusById, source.id);
        setTestingId(null);
        completeIndexerTest(source.id);
      }, wait);
    }
  }, [setErr, syncingId, syncStatusById]);

  return {
    syncingId,
    syncStatusById,
    syncAllRunning,
    testingId,
    testStatusById,
    hasPendingSync,
    syncAll,
    syncSource,
    testSource,
  };
}
