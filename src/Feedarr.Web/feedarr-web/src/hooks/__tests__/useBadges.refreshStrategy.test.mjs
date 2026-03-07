import test from "node:test";
import assert from "node:assert/strict";
import {
  createBadgeSseRefreshScheduler,
  resolveSystemToneFromSummary,
  runSummaryRefreshWithFallback,
} from "../useBadges.js";

function createFakeClock() {
  let nowMs = 0;
  let nextId = 1;
  const timers = new Map();

  const setTimer = (cb, ms) => {
    const id = nextId++;
    timers.set(id, { at: nowMs + Math.max(0, Number(ms) || 0), cb });
    return id;
  };

  const clearTimer = (id) => {
    timers.delete(id);
  };

  const advance = (ms) => {
    nowMs += Math.max(0, Number(ms) || 0);
    let progressed = true;
    while (progressed) {
      progressed = false;
      const due = [...timers.entries()]
        .filter(([, timer]) => timer.at <= nowMs)
        .sort((a, b) => a[1].at - b[1].at || a[0] - b[0]);

      for (const [id, timer] of due) {
        timers.delete(id);
        timer.cb();
        progressed = true;
      }
    }
  };

  return {
    now: () => nowMs,
    setTimer,
    clearTimer,
    advance,
  };
}

test("SSE burst is coalesced to one refresh per throttle window", async () => {
  const clock = createFakeClock();
  let refreshCalls = 0;

  const scheduler = createBadgeSseRefreshScheduler(
    () => { refreshCalls++; },
    {
      minIntervalMs: 1000,
      now: clock.now,
      setTimer: clock.setTimer,
      clearTimer: clock.clearTimer,
    }
  );

  for (let i = 0; i < 10; i++) {
    scheduler.trigger();
  }
  await Promise.resolve();

  assert.equal(refreshCalls, 1, "burst in same instant must trigger one refresh");

  for (let i = 0; i < 5; i++) {
    scheduler.trigger();
  }
  clock.advance(999);
  await Promise.resolve();
  assert.equal(refreshCalls, 1, "still inside throttle window");

  clock.advance(1);
  await Promise.resolve();
  assert.equal(refreshCalls, 2, "one additional refresh after throttle window");

  scheduler.dispose();
});

test("summary fallback switches to legacy path when summary endpoint is missing (404)", async () => {
  const state = { legacyOnly: false };
  let summaryCalls = 0;
  let legacyCalls = 0;

  const runSummary = async () => {
    summaryCalls++;
    const err = new Error("Not found");
    err.status = 404;
    throw err;
  };
  const runLegacy = async () => {
    legacyCalls++;
    return "legacy";
  };

  await runSummaryRefreshWithFallback({ state, runSummary, runLegacy });
  assert.equal(summaryCalls, 1);
  assert.equal(legacyCalls, 1);
  assert.equal(state.legacyOnly, true);

  await runSummaryRefreshWithFallback({ state, runSummary, runLegacy });
  assert.equal(summaryCalls, 1, "summary must not be called again once disabled");
  assert.equal(legacyCalls, 2);
});

test("summary system tone is recalculated from current payload", () => {
  assert.equal(resolveSystemToneFromSummary({ tone: "warn" }), "warn");
  assert.equal(resolveSystemToneFromSummary({ status: "error" }), "error");
  assert.equal(resolveSystemToneFromSummary({ warnings: 1 }), "warn");
  assert.equal(resolveSystemToneFromSummary({ errors: 2 }), "error");
});

test("summary system tone no longer depends on previous badge state", () => {
  const previousSystemTone = "error";
  const currentPayloadTone = resolveSystemToneFromSummary({ status: "warn" });

  assert.equal(currentPayloadTone, "warn");
  assert.notEqual(currentPayloadTone, previousSystemTone);
});

test("summary system tone ignores updatesBadge flag (no impact on updates behavior)", () => {
  assert.equal(resolveSystemToneFromSummary({ updatesBadge: true }), null);
  assert.equal(resolveSystemToneFromSummary({ updatesBadge: false }), null);
});
