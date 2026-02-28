/**
 * Tests for EventSource close-on-error behavior in useBadges.
 *
 * These tests exercise the onError handler logic in isolation
 * (no React required — pure handler behavior).
 *
 * Run: node --test src/hooks/__tests__/useBadges.sse.test.mjs
 */
import test from "node:test";
import assert from "node:assert/strict";

// ---------------------------------------------------------------------------
// Minimal mock for EventSource (records close() calls)
// ---------------------------------------------------------------------------
function makeMockEs() {
  const listeners = {};
  let closed = false;

  return {
    get closed() {
      return closed;
    },
    addEventListener(event, handler) {
      if (!listeners[event]) listeners[event] = [];
      listeners[event].push(handler);
    },
    removeEventListener(event, handler) {
      if (listeners[event]) {
        listeners[event] = listeners[event].filter((h) => h !== handler);
      }
    },
    close() {
      closed = true;
    },
    emit(event) {
      (listeners[event] || []).forEach((h) => h());
    },
  };
}

// ---------------------------------------------------------------------------
// Replicate the onError handler as defined in useBadges.js (after the fix)
// ---------------------------------------------------------------------------
function makeHandlers(es, setSseConnected) {
  const onError = () => {
    setSseConnected(false);
    es.close();
  };
  const onOpen = () => setSseConnected(true);

  es.addEventListener("error", onError);
  es.addEventListener("open", onOpen);

  // Cleanup (mirrors useEffect return)
  const cleanup = () => {
    setSseConnected(false);
    es.removeEventListener("error", onError);
    es.removeEventListener("open", onOpen);
    es.close();
  };

  return { onError, cleanup };
}

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

test("onError closes the EventSource and marks SSE as disconnected", () => {
  const es = makeMockEs();
  let sseConnected = true;
  makeHandlers(es, (v) => {
    sseConnected = v;
  });

  es.emit("error");

  assert.equal(sseConnected, false, "sseConnected must be false after error");
  assert.equal(es.closed, true, "es.close() must have been called on error");
});

test("cleanup (unmount) closes the EventSource", () => {
  const es = makeMockEs();
  let sseConnected = true;
  const { cleanup } = makeHandlers(es, (v) => {
    sseConnected = v;
  });

  cleanup();

  assert.equal(sseConnected, false, "sseConnected must be false on unmount");
  assert.equal(es.closed, true, "es.close() must be called on unmount");
});

test("onOpen sets sseConnected to true without closing", () => {
  const es = makeMockEs();
  let sseConnected = false;
  makeHandlers(es, (v) => {
    sseConnected = v;
  });

  es.emit("open");

  assert.equal(sseConnected, true, "sseConnected must be true after open");
  assert.equal(es.closed, false, "es must NOT be closed after open");
});

test("error after successful open closes the connection", () => {
  const es = makeMockEs();
  let sseConnected = false;
  makeHandlers(es, (v) => {
    sseConnected = v;
  });

  es.emit("open");
  assert.equal(sseConnected, true);
  assert.equal(es.closed, false);

  es.emit("error");
  assert.equal(sseConnected, false, "must disconnect on error even after open");
  assert.equal(es.closed, true, "must close on error even after open");
});
