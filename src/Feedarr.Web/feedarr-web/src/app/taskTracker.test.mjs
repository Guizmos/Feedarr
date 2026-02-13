import test from "node:test";
import assert from "node:assert/strict";
import { addTask, getTasks, removeTask, updateTask } from "./taskTracker.js";

function createMockWindow() {
  const storage = new Map();
  const events = [];

  return {
    storage,
    events,
    window: {
      localStorage: {
        getItem(key) {
          return storage.has(key) ? storage.get(key) : null;
        },
        setItem(key, value) {
          storage.set(key, String(value));
        },
        removeItem(key) {
          storage.delete(key);
        },
      },
      dispatchEvent(event) {
        events.push(event?.type || "");
        return true;
      },
    },
  };
}

test("taskTracker add/update/remove lifecycle", () => {
  const originalWindow = globalThis.window;
  const originalEvent = globalThis.Event;
  const mock = createMockWindow();

  globalThis.Event = class EventMock {
    constructor(type) {
      this.type = type;
    }
  };
  globalThis.window = mock.window;

  try {
    addTask({ key: "rss-sync-all", label: "Sync", meta: "0/10", ttlMs: 60_000 });
    let tasks = getTasks();
    assert.equal(tasks.length, 1);
    assert.equal(tasks[0].key, "rss-sync-all");
    assert.equal(tasks[0].meta, "0/10");

    updateTask("rss-sync-all", { meta: "4/10" });
    tasks = getTasks();
    assert.equal(tasks[0].meta, "4/10");

    removeTask("rss-sync-all");
    tasks = getTasks();
    assert.equal(tasks.length, 0);

    assert.ok(mock.events.length >= 3);
    assert.ok(mock.events.every((eventType) => eventType === "tasks:updated"));
  } finally {
    globalThis.window = originalWindow;
    globalThis.Event = originalEvent;
  }
});

test("taskTracker ignores expired tasks from storage", () => {
  const originalWindow = globalThis.window;
  const originalEvent = globalThis.Event;
  const mock = createMockWindow();

  globalThis.Event = class EventMock {
    constructor(type) {
      this.type = type;
    }
  };
  globalThis.window = mock.window;

  try {
    const now = Date.now();
    mock.window.localStorage.setItem(
      "feedarr:tasks",
      JSON.stringify([
        { key: "old", label: "old", startedAt: now - 5_000, expiresAt: now - 1_000 },
        { key: "live", label: "live", startedAt: now - 1_000, expiresAt: now + 10_000 },
      ])
    );

    const tasks = getTasks();
    assert.equal(tasks.length, 1);
    assert.equal(tasks[0].key, "live");
  } finally {
    globalThis.window = originalWindow;
    globalThis.Event = originalEvent;
  }
});
