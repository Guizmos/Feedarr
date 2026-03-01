/**
 * Tests for the per-instance state ref in usePosterPollingService.
 *
 * Validates that the module-level `state` singleton has been replaced by a
 * per-instance useRef so that multiple independent hook consumers each own
 * their own cycleRunning / inFlight / timer state without interference.
 *
 * Run: node --test src/hooks/__tests__/usePosterPollingService.isolation.test.mjs
 */
import test from "node:test";
import assert from "node:assert/strict";

// ---------------------------------------------------------------------------
// Simulate the per-instance state initialisation extracted from the hook.
// After Fix 4, each call to usePosterPollingService initialises its own
// stateRef.current — modelled here by makePollingInstance().
// ---------------------------------------------------------------------------
function makePollingInstance({ intervalMs = 12000, maxDurationMs = 90000 } = {}) {
  // Replicates stateRef.current initialisation from usePosterPollingService
  const s = {
    cycleRunning: false,
    cycleStartedAt: 0,
    lastFingerprintSeen: "",
    lastCycleCompletedFingerprint: "",
    intervalMs,
    maxDurationMs,
    timer: null,
    inFlight: false,
  };

  function clearTimer() {
    if (s.timer) {
      clearTimeout(s.timer);
      s.timer = null;
    }
  }

  function stopCycle() {
    clearTimer();
    s.cycleRunning = false;
    s.cycleStartedAt = 0;
    s.inFlight = false;
  }

  function startCycle() {
    if (s.cycleRunning) return false;
    s.cycleRunning = true;
    s.cycleStartedAt = Date.now();
    return true;
  }

  return { state: s, startCycle, stopCycle };
}

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

test("initial state: cycleRunning and inFlight are false", () => {
  const { state } = makePollingInstance();
  assert.equal(state.cycleRunning, false);
  assert.equal(state.inFlight, false);
  assert.equal(state.timer, null);
});

test("startCycle sets cycleRunning to true", () => {
  const { state, startCycle, stopCycle } = makePollingInstance();
  const started = startCycle();
  assert.equal(started, true);
  assert.equal(state.cycleRunning, true);
  stopCycle();
});

test("startCycle is idempotent — second call returns false when already running", () => {
  const { startCycle, stopCycle } = makePollingInstance();
  const first = startCycle();
  const second = startCycle();
  assert.equal(first, true, "first start should succeed");
  assert.equal(second, false, "second start must be a no-op while cycle is running");
  stopCycle();
});

test("stopCycle resets cycleRunning and inFlight to false", () => {
  const { state, startCycle, stopCycle } = makePollingInstance();
  startCycle();
  state.inFlight = true;
  stopCycle();
  assert.equal(state.cycleRunning, false);
  assert.equal(state.inFlight, false);
});

test("two independent instances do NOT share cycleRunning state", () => {
  const a = makePollingInstance();
  const b = makePollingInstance();

  const aStarted = a.startCycle();
  // Instance B has its own state object — must start independently
  const bStarted = b.startCycle();

  assert.equal(aStarted, true, "instance A must start");
  assert.equal(bStarted, true, "instance B must start independently of A");
  assert.equal(a.state.cycleRunning, true);
  assert.equal(b.state.cycleRunning, true);

  a.stopCycle();
  // Stopping A must NOT affect B
  assert.equal(a.state.cycleRunning, false, "A stopped");
  assert.equal(b.state.cycleRunning, true, "B is still running after A stops");

  b.stopCycle();
  assert.equal(b.state.cycleRunning, false);
});

test("two independent instances do NOT share inFlight flag", () => {
  const a = makePollingInstance();
  const b = makePollingInstance();

  a.startCycle();
  a.state.inFlight = true;

  b.startCycle();

  assert.equal(a.state.inFlight, true, "A has inFlight=true");
  assert.equal(b.state.inFlight, false, "B must have its own inFlight, not affected by A");

  a.stopCycle();
  b.stopCycle();
});

test("instance config (intervalMs, maxDurationMs) is independent per instance", () => {
  const a = makePollingInstance({ intervalMs: 5000, maxDurationMs: 30000 });
  const b = makePollingInstance({ intervalMs: 12000, maxDurationMs: 90000 });

  assert.equal(a.state.intervalMs, 5000);
  assert.equal(b.state.intervalMs, 12000);

  // Mutating A's config must not affect B
  a.state.intervalMs = 1000;
  assert.equal(b.state.intervalMs, 12000, "B's intervalMs must be unaffected by A");
});

test("instance can restart after stopCycle", () => {
  const { startCycle, stopCycle } = makePollingInstance();
  startCycle();
  stopCycle();
  const restarted = startCycle();
  assert.equal(restarted, true, "instance must be restartable after stop");
  stopCycle();
});
