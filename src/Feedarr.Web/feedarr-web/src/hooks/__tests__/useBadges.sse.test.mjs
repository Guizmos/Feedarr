import test from "node:test";
import assert from "node:assert/strict";
import { createBadgeSseConnector } from "../useBadges.js";

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
    setTimer,
    clearTimer,
    advance,
  };
}

function makeMockEventSource(url) {
  const listeners = {};
  let closed = false;

  return {
    url,
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

function countActiveConnections(instances) {
  return instances.filter((it) => !it.closed).length;
}

test("SSE error triggers reconnect with backoff (no manual page reload)", () => {
  const clock = createFakeClock();
  const instances = [];
  let connected = false;

  const connector = createBadgeSseConnector({
    url: "/api/badges/stream",
    reconnectBaseMs: 1000,
    reconnectMaxMs: 5000,
    setTimer: clock.setTimer,
    clearTimer: clock.clearTimer,
    onConnected: () => { connected = true; },
    onDisconnected: () => { connected = false; },
    onSignal: () => {},
    createEventSource: (url) => {
      const es = makeMockEventSource(url);
      instances.push(es);
      return es;
    },
  });

  assert.equal(instances.length, 1);
  instances[0].emit("open");
  assert.equal(connected, true);

  instances[0].emit("error");
  assert.equal(connected, false, "must mark disconnected on SSE error");
  assert.equal(instances[0].closed, true, "must close broken connection before reconnect");

  clock.advance(999);
  assert.equal(instances.length, 1, "must wait reconnect backoff");

  clock.advance(1);
  assert.equal(instances.length, 2, "must reconnect after backoff");
  instances[1].emit("open");
  assert.equal(connected, true, "must reconnect without manual reload");

  connector.dispose();
});

test("after reconnect, next SSE event still triggers badges refresh", async () => {
  const clock = createFakeClock();
  const instances = [];
  let refreshCalls = 0;

  const connector = createBadgeSseConnector({
    url: "/api/badges/stream",
    reconnectBaseMs: 400,
    reconnectMaxMs: 1200,
    setTimer: clock.setTimer,
    clearTimer: clock.clearTimer,
    onSignal: () => { refreshCalls++; },
    createEventSource: (url) => {
      const es = makeMockEventSource(url);
      instances.push(es);
      return es;
    },
  });

  assert.equal(instances.length, 1);
  instances[0].emit("error");
  clock.advance(400);
  assert.equal(instances.length, 2, "a new EventSource must be created after error");

  instances[1].emit("badges-changed");
  await Promise.resolve();
  assert.equal(refreshCalls, 1, "badge signal after reconnect must refresh");

  connector.dispose();
});

test("connector prevents multiple concurrent EventSource leaks", () => {
  const clock = createFakeClock();
  const instances = [];

  const connector = createBadgeSseConnector({
    url: "/api/badges/stream",
    reconnectBaseMs: 300,
    reconnectMaxMs: 300,
    setTimer: clock.setTimer,
    clearTimer: clock.clearTimer,
    onSignal: () => {},
    createEventSource: (url) => {
      const es = makeMockEventSource(url);
      instances.push(es);
      return es;
    },
  });

  assert.equal(instances.length, 1);
  assert.equal(countActiveConnections(instances), 1);

  instances[0].emit("error");
  instances[0].emit("error");
  assert.equal(countActiveConnections(instances), 0, "broken connection must be closed");

  clock.advance(300);
  assert.equal(instances.length, 2, "only one reconnect must be scheduled");
  assert.equal(countActiveConnections(instances), 1);

  instances[1].emit("error");
  instances[1].emit("error");
  clock.advance(300);
  assert.equal(instances.length, 3, "still one new connection per reconnect cycle");
  assert.equal(countActiveConnections(instances), 1);

  connector.dispose();
  assert.equal(countActiveConnections(instances), 0, "dispose must close active connection");
  clock.advance(3000);
  assert.equal(instances.length, 3, "dispose must prevent further reconnects");
});
