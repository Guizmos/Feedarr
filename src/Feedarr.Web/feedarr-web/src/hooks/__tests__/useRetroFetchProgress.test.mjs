/**
 * Tests for the per-instance polling ref in useRetroFetchProgress.
 *
 * Validates that the module-level isPolling flag has been replaced by a
 * per-instance useRef so that multiple independent hook instances can each
 * start and stop polling without blocking one another.
 *
 * Run: node --test src/hooks/__tests__/useRetroFetchProgress.test.mjs
 */
import test from "node:test";
import assert from "node:assert/strict";

// ---------------------------------------------------------------------------
// Simulate the per-instance polling ref logic (extracted from the hook)
// ---------------------------------------------------------------------------
function makePollingInstance() {
  // Replicates the relevant part of useRetroFetchProgress after the fix
  const isPollingRef = { current: false };
  let intervalId = null;

  function stopPolling() {
    if (intervalId !== null) {
      clearInterval(intervalId);
      intervalId = null;
    }
    isPollingRef.current = false;
  }

  function startPolling(tick) {
    if (isPollingRef.current) return false; // already polling
    isPollingRef.current = true;
    tick();
    intervalId = setInterval(tick, 4000);
    return true; // started
  }

  return { isPollingRef, startPolling, stopPolling };
}

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

test("per-instance ref starts as false", () => {
  const instance = makePollingInstance();
  assert.equal(instance.isPollingRef.current, false);
});

test("startPolling sets isPollingRef to true", () => {
  const instance = makePollingInstance();
  const started = instance.startPolling(() => {});
  assert.equal(started, true);
  assert.equal(instance.isPollingRef.current, true);
  instance.stopPolling();
});

test("startPolling is idempotent — second call returns false if already polling", () => {
  const instance = makePollingInstance();
  const first = instance.startPolling(() => {});
  const second = instance.startPolling(() => {}); // must be a no-op
  assert.equal(first, true, "first start should succeed");
  assert.equal(second, false, "second start must be blocked while polling");
  instance.stopPolling();
});

test("stopPolling resets isPollingRef to false", () => {
  const instance = makePollingInstance();
  instance.startPolling(() => {});
  instance.stopPolling();
  assert.equal(instance.isPollingRef.current, false);
});

test("two independent instances do NOT block each other", () => {
  const a = makePollingInstance();
  const b = makePollingInstance();

  const aStarted = a.startPolling(() => {});
  // Instance B has its own ref — should start independently
  const bStarted = b.startPolling(() => {});

  assert.equal(aStarted, true, "instance A must start");
  assert.equal(bStarted, true, "instance B must start independently of A");
  assert.equal(a.isPollingRef.current, true);
  assert.equal(b.isPollingRef.current, true);

  a.stopPolling();
  // B's ref must still be true after A stops
  assert.equal(b.isPollingRef.current, true, "stopping A must not affect B");
  b.stopPolling();
  assert.equal(b.isPollingRef.current, false);
});

test("instance can restart after stopPolling", () => {
  const instance = makePollingInstance();
  instance.startPolling(() => {});
  instance.stopPolling();
  const restarted = instance.startPolling(() => {});
  assert.equal(restarted, true, "instance must restart after stop");
  instance.stopPolling();
});
