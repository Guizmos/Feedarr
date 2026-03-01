/**
 * Tests for AbortController ref tracking in CategoryPreviewModal.
 *
 * Exercises the abortCtrlRef logic in isolation — verifies that:
 *  - the initial fetch is aborted on cleanup
 *  - a retry aborts the previous in-flight request before starting a new one
 *
 * Run: node --test src/components/shared/__tests__/categoryPreviewModal.abortRef.test.mjs
 */
import test from "node:test";
import assert from "node:assert/strict";

// ---------------------------------------------------------------------------
// Minimal AbortController replica (Node's built-in is fine, but we track
// abort() calls explicitly to keep assertions clear)
// ---------------------------------------------------------------------------
function makeController() {
  let aborted = false;
  const listeners = [];
  const signal = {
    get aborted() { return aborted; },
    addEventListener(_ev, fn) { listeners.push(fn); },
    removeEventListener(_ev, fn) {
      const i = listeners.indexOf(fn);
      if (i !== -1) listeners.splice(i, 1);
    },
  };
  return {
    signal,
    abort() {
      if (aborted) return;
      aborted = true;
      listeners.forEach((fn) => fn());
    },
    get aborted() { return aborted; },
  };
}

// ---------------------------------------------------------------------------
// Simulate the abortCtrlRef pattern extracted from CategoryPreviewModal
// ---------------------------------------------------------------------------
function makeModalAbortLogic(fetchData) {
  let abortCtrlRef = null;

  // Mirrors useEffect (cleanup uses ref, not closure variable)
  function mount() {
    const controller = makeController();
    abortCtrlRef = controller;
    fetchData(controller.signal);

    return function cleanup() {
      // Match the component: abort whatever is currently in the ref, not the
      // initial controller (which may have been superseded by a retry call).
      if (abortCtrlRef) abortCtrlRef.abort();
      abortCtrlRef = null;
    };
  }

  // Mirrors the retry button onClick
  function retry() {
    abortCtrlRef?.abort();
    const controller = makeController();
    abortCtrlRef = controller;
    fetchData(controller.signal);
  }

  return { mount, retry, getRef: () => abortCtrlRef };
}

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

test("cleanup aborts the initial in-flight controller", () => {
  const signals = [];
  const fetchData = (signal) => signals.push(signal);

  const { mount } = makeModalAbortLogic(fetchData);
  const cleanup = mount();

  assert.equal(signals.length, 1);
  assert.equal(signals[0].aborted, false, "signal not aborted before cleanup");

  cleanup();

  assert.equal(signals[0].aborted, true, "signal must be aborted after cleanup");
});

test("retry aborts the previous in-flight controller before starting new fetch", () => {
  const signals = [];
  const fetchData = (signal) => signals.push(signal);

  const { mount, retry } = makeModalAbortLogic(fetchData);
  mount();

  assert.equal(signals.length, 1);
  const firstSignal = signals[0];

  retry();

  assert.equal(signals.length, 2, "retry must trigger a second fetch");
  assert.equal(firstSignal.aborted, true, "first controller must be aborted on retry");
  assert.equal(signals[1].aborted, false, "new controller starts un-aborted");
});

test("cleanup after retry aborts only the latest controller", () => {
  const signals = [];
  const fetchData = (signal) => signals.push(signal);

  const { mount, retry } = makeModalAbortLogic(fetchData);
  const cleanup = mount();
  retry();

  const [first, second] = signals;
  assert.equal(first.aborted, true, "first already aborted by retry");
  assert.equal(second.aborted, false, "second not aborted yet");

  cleanup();

  assert.equal(second.aborted, true, "cleanup must abort the second (latest) controller");
});

test("ref is null after cleanup so a stale retry is a no-op", () => {
  const signals = [];
  const fetchData = (signal) => signals.push(signal);

  const { mount, retry } = makeModalAbortLogic(fetchData);
  const cleanup = mount();
  cleanup();

  // Calling retry after unmount — ref is null, so abort() is not called on anything
  assert.doesNotThrow(() => retry());
  // A new fetch IS started (retry always creates a new controller)
  assert.equal(signals.length, 2);
});
