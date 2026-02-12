import { useEffect } from "react";
import { apiGet } from "../api/client.js";

const TRIGGER_EVENT = "posters:trigger";
const TICK_EVENT = "posters:polling:tick";

const state = {
  cycleRunning: false,
  cycleStartedAt: 0,
  lastFingerprintSeen: "",
  lastCycleCompletedFingerprint: "",
  intervalMs: 12000,
  maxDurationMs: 90000,
  timer: null,
  inFlight: false,
};

function log(message, data) {
  try {
    if (data !== undefined) console.log(`[PosterPolling] ${message}`, data);
    else console.log(`[PosterPolling] ${message}`);
  } catch {
    // ignore logging errors
  }
}

function clearTimer() {
  if (state.timer) {
    clearTimeout(state.timer);
    state.timer = null;
  }
}

function dispatchTick(detail) {
  if (typeof window === "undefined") return;
  window.dispatchEvent(new CustomEvent(TICK_EVENT, { detail }));
}

async function fetchStats() {
  try {
    return await apiGet("/api/posters/stats");
  } catch {
    return null;
  }
}

function stopCycle(reason, stats) {
  clearTimer();
  state.cycleRunning = false;
  state.cycleStartedAt = 0;
  state.inFlight = false;
  const fingerprint = stats?.stateFingerprint ? String(stats.stateFingerprint) : state.lastFingerprintSeen;
  if (fingerprint) state.lastCycleCompletedFingerprint = fingerprint;
  log("stop", { reason, fingerprint });
  dispatchTick({
    reason: `stop:${reason}`,
    stats: stats || null,
    fingerprint: fingerprint || "",
    fingerprintChanged: false,
    cycleRunning: false,
    cycleStartedAtTs: 0,
  });
}

function processStats(stats, reason) {
  if (!stats) {
    const elapsed = Date.now() - state.cycleStartedAt;
    dispatchTick({
      reason,
      stats: null,
      fingerprint: state.lastFingerprintSeen,
      fingerprintChanged: false,
      cycleRunning: state.cycleRunning,
      cycleStartedAtTs: state.cycleStartedAt,
    });
    if (elapsed >= state.maxDurationMs) {
      stopCycle("timeout");
      return false;
    }
    return true;
  }

  const missingActionable = Number(stats.missingActionable || 0);
  const fingerprint = stats.stateFingerprint ? String(stats.stateFingerprint) : "";
  const fingerprintChanged = !!fingerprint && fingerprint !== state.lastFingerprintSeen;
  if (fingerprint) state.lastFingerprintSeen = fingerprint;

  dispatchTick({
    reason,
    stats,
    fingerprint,
    fingerprintChanged,
    cycleRunning: state.cycleRunning,
    cycleStartedAtTs: state.cycleStartedAt,
  });

  if (missingActionable <= 0) {
    stopCycle("empty", stats);
    return false;
  }

  const elapsed = Date.now() - state.cycleStartedAt;
  if (elapsed >= state.maxDurationMs) {
    stopCycle("timeout", stats);
    return false;
  }

  return true;
}

async function runTick(reason = "tick") {
  if (!state.cycleRunning || state.inFlight) return;
  state.inFlight = true;
  const stats = await fetchStats();
  state.inFlight = false;
  if (!state.cycleRunning) return;
  if (processStats(stats, reason)) {
    clearTimer();
    state.timer = setTimeout(() => runTick("interval"), state.intervalMs);
  }
}

function startCycle(reason, initialStats) {
  if (state.cycleRunning) return;
  state.cycleRunning = true;
  state.cycleStartedAt = Date.now();
  log("start", { reason, fingerprint: initialStats?.stateFingerprint || "" });

  if (processStats(initialStats, "start")) {
    clearTimer();
    state.timer = setTimeout(() => runTick("interval"), state.intervalMs);
  }
}

export function triggerPosterPolling(reason = "manual") {
  if (typeof window === "undefined") return;
  window.dispatchEvent(new CustomEvent(TRIGGER_EVENT, { detail: { reason } }));
}

export function usePosterPollingService({
  intervalMs = 12000,
  maxDurationMs = 90000,
  enabled = true,
} = {}) {
  useEffect(() => {
    if (!enabled || typeof window === "undefined") return undefined;
    state.intervalMs = intervalMs;
    state.maxDurationMs = maxDurationMs;

    const onTrigger = async (event) => {
      if (state.cycleRunning) {
        log("skip trigger (cycle running)", { reason: event?.detail?.reason });
        return;
      }

      const stats = await fetchStats();
      if (!stats) {
        log("skip trigger (stats unavailable)");
        return;
      }

      const missingActionable = Number(stats.missingActionable || 0);
      const fingerprint = stats.stateFingerprint ? String(stats.stateFingerprint) : "";
      if (fingerprint) state.lastFingerprintSeen = fingerprint;

      if (missingActionable <= 0) {
        if (fingerprint) state.lastCycleCompletedFingerprint = fingerprint;
        log("skip trigger (nothing actionable)", { fingerprint });
        return;
      }

      if (fingerprint && fingerprint === state.lastCycleCompletedFingerprint) {
        log("skip trigger (unchanged)", { fingerprint });
        return;
      }

      startCycle(event?.detail?.reason || "trigger", stats);
    };

    window.addEventListener(TRIGGER_EVENT, onTrigger);
    return () => window.removeEventListener(TRIGGER_EVENT, onTrigger);
  }, [intervalMs, maxDurationMs, enabled]);
}
