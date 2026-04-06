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
 * Creates a reconnecting EventSource connector for badge signals.
 *
 * @param {{
 *   url: string,
 *   reconnectBaseMs?: number,
 *   reconnectMaxMs?: number,
 *   setTimer?: (cb: () => void, ms: number) => any,
 *   clearTimer?: (id: any) => void,
 *   onConnected?: () => void,
 *   onDisconnected?: () => void,
 *   onSignal?: () => void,
 *   createEventSource?: (url: string) => EventSourceLike,
 * }} options
 * @returns {{ dispose: () => void }}
 */
export function createBadgeSseConnector({
  url,
  reconnectBaseMs = 1000,
  reconnectMaxMs = 10000,
  setTimer = (cb, ms) => setTimeout(cb, ms),
  clearTimer = (id) => clearTimeout(id),
  onConnected = () => {},
  onDisconnected = () => {},
  onSignal = () => {},
  createEventSource = (nextUrl) => new EventSource(nextUrl, { withCredentials: true }),
} = {}) {
  const baseMs = Math.max(100, Number(reconnectBaseMs) || 1000);
  const maxMs = Math.max(baseMs, Number(reconnectMaxMs) || baseMs);

  let source = null;
  let reconnectTimerId = null;
  let reconnectAttempt = 0;
  let connected = false;
  let disposed = false;

  let onOpenHandler = null;
  let onReadyHandler = null;
  let onBadgeHandler = null;
  let onBadgesChangedHandler = null;
  let onErrorHandler = null;

  const setConnected = (nextConnected) => {
    if (connected === nextConnected) return;
    connected = nextConnected;
    if (connected) onConnected();
    else onDisconnected();
  };

  const clearReconnectTimer = () => {
    if (reconnectTimerId == null) return;
    clearTimer(reconnectTimerId);
    reconnectTimerId = null;
  };

  const detachAndCloseSource = () => {
    if (!source) return;

    source.removeEventListener("open", onOpenHandler);
    source.removeEventListener("ready", onReadyHandler);
    source.removeEventListener("badge", onBadgeHandler);
    source.removeEventListener("badges-changed", onBadgesChangedHandler);
    source.removeEventListener("error", onErrorHandler);
    source.close();

    source = null;
    onOpenHandler = null;
    onReadyHandler = null;
    onBadgeHandler = null;
    onBadgesChangedHandler = null;
    onErrorHandler = null;
  };

  const scheduleReconnect = () => {
    if (disposed || reconnectTimerId != null) return;

    const delayMs = Math.min(maxMs, baseMs * Math.pow(2, reconnectAttempt));
    reconnectAttempt += 1;

    reconnectTimerId = setTimer(() => {
      reconnectTimerId = null;
      if (disposed) return;
      connect();
    }, delayMs);
  };

  const handleSignal = () => {
    onSignal();
  };

  const handleError = () => {
    setConnected(false);
    detachAndCloseSource();
    scheduleReconnect();
  };

  const handleConnected = () => {
    reconnectAttempt = 0;
    setConnected(true);
  };

  const connect = () => {
    if (disposed || source) return;

    source = createEventSource(url);
    onOpenHandler = () => handleConnected();
    onReadyHandler = () => {
      handleConnected();
      handleSignal();
    };
    onBadgeHandler = handleSignal;
    onBadgesChangedHandler = handleSignal;
    onErrorHandler = handleError;

    source.addEventListener("open", onOpenHandler);
    source.addEventListener("ready", onReadyHandler);
    source.addEventListener("badge", onBadgeHandler);
    source.addEventListener("badges-changed", onBadgesChangedHandler);
    source.addEventListener("error", onErrorHandler);
  };

  connect();

  return {
    dispose() {
      if (disposed) return;
      disposed = true;
      clearReconnectTimer();
      setConnected(false);
      detachAndCloseSource();
    },
  };
}

export function canUseBadgeSseEnvironment(
  globalWindow = typeof window === "undefined" ? undefined : window,
  eventSourceCtor = typeof EventSource === "undefined" ? undefined : EventSource
) {
  return typeof globalWindow !== "undefined" && typeof eventSourceCtor !== "undefined";
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
    if (!canUseBadgeSseEnvironment()) return undefined;

    const url = resolveApiUrl("/api/badges/stream");
    const scheduler = createBadgeSseRefreshScheduler(
      () => refreshRef.current?.(),
      { minIntervalMs: 1000 }
    );
    const connector = createBadgeSseConnector({
      url,
      reconnectBaseMs: 1000,
      reconnectMaxMs: 10000,
      onConnected: () => setSseConnected(true),
      onDisconnected: () => setSseConnected(false),
      onSignal: () => scheduler.trigger(),
    });

    return () => {
      setSseConnected(false);
      scheduler.dispose();
      connector.dispose();
    };
  }, [refreshRef]); // stable ref from caller; keep effect tied to that reference

  return { sseConnected };
}
