import { useEffect, useState } from "react";
import { resolveApiUrl } from "../api/client.js";

/**
 * Coalescing SSE refresh scheduler.
 *
 * Ensures that a burst of badge-changed SSE events results in at most one
 * `refresh()` call per `minIntervalMs` window, while still firing immediately
 * when enough time has elapsed since the last run.
 *
 * @param {() => void} refresh
 * @param {{ minIntervalMs?: number, now?: () => number, setTimer?: Function, clearTimer?: Function }} [options]
 * @returns {{ trigger: () => void, dispose: () => void }}
 */
export function createBadgeSseRefreshScheduler(
  refresh,
  {
    minIntervalMs = 1000,
    now = () => Date.now(),
    setTimer = (cb, ms) => setTimeout(cb, ms),
    clearTimer = (id) => clearTimeout(id),
  } = {}
) {
  const intervalMs = Math.max(250, Number(minIntervalMs) || 1000);
  let timerId = null;
  let hasRun = false;
  let lastRunAt = 0;
  let pending = false;
  let disposed = false;

  const invoke = () => {
    if (disposed) return;
    pending = false;
    hasRun = true;
    lastRunAt = now();
    Promise.resolve()
      .then(() => refresh())
      .catch(() => {});
  };

  const schedule = () => {
    if (disposed || !pending || timerId != null) return;

    const elapsed = hasRun ? now() - lastRunAt : Number.POSITIVE_INFINITY;
    const waitMs = elapsed >= intervalMs ? 0 : intervalMs - elapsed;

    if (waitMs <= 0) {
      invoke();
      return;
    }

    timerId = setTimer(() => {
      timerId = null;
      if (!pending || disposed) return;
      invoke();
    }, waitMs);
  };

  return {
    trigger() {
      if (disposed) return;
      pending = true;
      schedule();
    },
    dispose() {
      disposed = true;
      pending = false;
      if (timerId != null) {
        clearTimer(timerId);
        timerId = null;
      }
    },
  };
}

/**
 * Manages the EventSource connection to `/api/badges/stream`.
 *
 * - Opens once on mount, never recreated.
 * - Closes on error (SSE will reconnect via polling instead).
 * - Fires `refreshRef.current()` on `ready` events and coalesces `badge` /
 *   `badges-changed` bursts through `createBadgeSseRefreshScheduler`.
 *
 * @param {{ current: (() => void) | null }} refreshRef  Stable ref pointing to the current refresh callback.
 * @returns {{ sseConnected: boolean }}
 */
export default function useBadgeSse(refreshRef) {
  const [sseConnected, setSseConnected] = useState(false);

  useEffect(() => {
    if (typeof window === "undefined" || typeof EventSource === "undefined") return undefined;

    const url = resolveApiUrl("/api/badges/stream");
    const es = new EventSource(url, { withCredentials: true });
    const scheduler = createBadgeSseRefreshScheduler(
      () => refreshRef.current?.(),
      { minIntervalMs: 1000 }
    );

    const onReady = () => {
      setSseConnected(true);
      refreshRef.current?.();
    };
    const onBadgeChanged = () => scheduler.trigger();
    const onError = () => {
      setSseConnected(false);
      es.close();
    };
    const onOpen = () => setSseConnected(true);

    es.addEventListener("ready", onReady);
    es.addEventListener("badge", onBadgeChanged);
    es.addEventListener("badges-changed", onBadgeChanged);
    es.addEventListener("error", onError);
    es.addEventListener("open", onOpen);

    return () => {
      setSseConnected(false);
      scheduler.dispose();
      es.removeEventListener("ready", onReady);
      es.removeEventListener("badge", onBadgeChanged);
      es.removeEventListener("badges-changed", onBadgeChanged);
      es.removeEventListener("error", onError);
      es.removeEventListener("open", onOpen);
      es.close();
    };
  }, []); // stable: EventSource connection created once, never recreated

  return { sseConnected };
}
