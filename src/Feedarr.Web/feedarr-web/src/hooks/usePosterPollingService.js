import { useEffect, useRef } from "react";
import { apiGet } from "../api/client.js";

const TRIGGER_EVENT = "posters:trigger";
const TICK_EVENT = "posters:polling:tick";

function log(message, data) {
  if (!import.meta.env?.DEV) return;
  try {
    if (data !== undefined) console.log(`[PosterPolling] ${message}`, data);
    else console.log(`[PosterPolling] ${message}`);
  } catch {
    // ignore logging errors
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

export function triggerPosterPolling(reason = "manual") {
  if (typeof window === "undefined") return;
  window.dispatchEvent(new CustomEvent(TRIGGER_EVENT, { detail: { reason } }));
}

export function usePosterPollingService({
  intervalMs = 12000,
  maxDurationMs = 90000,
  enabled = true,
} = {}) {
  // Per-instance state — each hook consumer owns its own cycle independently.
  const stateRef = useRef(null);
  if (stateRef.current === null) {
    stateRef.current = {
      cycleRunning: false,
      cycleStartedAt: 0,
      lastFingerprintSeen: "",
      lastCycleCompletedFingerprint: "",
      intervalMs,
      maxDurationMs,
      timer: null,
      inFlight: false,
    };
  }

  useEffect(() => {
    if (!enabled || typeof window === "undefined") return undefined;

    const s = stateRef.current;
    s.intervalMs = intervalMs;
    s.maxDurationMs = maxDurationMs;

    function clearTimer() {
      if (s.timer) {
        clearTimeout(s.timer);
        s.timer = null;
      }
    }

    function stopCycle(reason, stats) {
      clearTimer();
      s.cycleRunning = false;
      s.cycleStartedAt = 0;
      s.inFlight = false;
      const fingerprint = stats?.stateFingerprint ? String(stats.stateFingerprint) : s.lastFingerprintSeen;
      if (fingerprint) s.lastCycleCompletedFingerprint = fingerprint;
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
        const elapsed = Date.now() - s.cycleStartedAt;
        dispatchTick({
          reason,
          stats: null,
          fingerprint: s.lastFingerprintSeen,
          fingerprintChanged: false,
          cycleRunning: s.cycleRunning,
          cycleStartedAtTs: s.cycleStartedAt,
        });
        if (elapsed >= s.maxDurationMs) {
          stopCycle("timeout");
          return false;
        }
        return true;
      }

      const missingActionable = Number(stats.missingActionable || 0);
      const fingerprint = stats.stateFingerprint ? String(stats.stateFingerprint) : "";
      const fingerprintChanged = !!fingerprint && fingerprint !== s.lastFingerprintSeen;
      if (fingerprint) s.lastFingerprintSeen = fingerprint;

      dispatchTick({
        reason,
        stats,
        fingerprint,
        fingerprintChanged,
        cycleRunning: s.cycleRunning,
        cycleStartedAtTs: s.cycleStartedAt,
      });

      if (missingActionable <= 0) {
        stopCycle("empty", stats);
        return false;
      }

      const elapsed = Date.now() - s.cycleStartedAt;
      if (elapsed >= s.maxDurationMs) {
        stopCycle("timeout", stats);
        return false;
      }

      return true;
    }

    async function runTick(reason = "tick") {
      if (!s.cycleRunning || s.inFlight) return;
      s.inFlight = true;
      const stats = await fetchStats();
      s.inFlight = false;
      if (!s.cycleRunning) return;
      if (processStats(stats, reason)) {
        clearTimer();
        s.timer = setTimeout(() => runTick("interval"), s.intervalMs);
      }
    }

    function startCycle(reason, initialStats) {
      if (s.cycleRunning) return;
      s.cycleRunning = true;
      s.cycleStartedAt = Date.now();
      log("start", { reason, fingerprint: initialStats?.stateFingerprint || "" });

      if (processStats(initialStats, "start")) {
        clearTimer();
        s.timer = setTimeout(() => runTick("interval"), s.intervalMs);
      }
    }

    const onTrigger = async (event) => {
      if (s.cycleRunning) {
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
      if (fingerprint) s.lastFingerprintSeen = fingerprint;

      if (missingActionable <= 0) {
        if (fingerprint) s.lastCycleCompletedFingerprint = fingerprint;
        log("skip trigger (nothing actionable)", { fingerprint });
        return;
      }

      if (fingerprint && fingerprint === s.lastCycleCompletedFingerprint) {
        log("skip trigger (unchanged)", { fingerprint });
        return;
      }

      startCycle(event?.detail?.reason || "trigger", stats);
    };

    window.addEventListener(TRIGGER_EVENT, onTrigger);
    return () => {
      window.removeEventListener(TRIGGER_EVENT, onTrigger);
      clearTimer();
      s.cycleRunning = false;
      s.inFlight = false;
    };
  }, [intervalMs, maxDurationMs, enabled]);
}
