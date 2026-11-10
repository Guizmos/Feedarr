import test from "node:test";
import assert from "node:assert/strict";
import { apiGet, apiPost } from "./client.js";
const deferred = () => { let resolve; const promise = new Promise(yes => { resolve = yes; }); return { promise, resolve }; };
const flush = async () => { for (let i = 0; i < 8; i++) await Promise.resolve(); };
const response = (body, status = 200) => ({ status, ok: status < 400, headers: new Headers({ "content-type": "application/json" }), json: () => body });
async function harness(action) {
  const original = { fetch: globalThis.fetch, setTimeout: globalThis.setTimeout, clearTimeout: globalThis.clearTimeout };
  const timers = new Map(); let id = 0;
  globalThis.setTimeout = callback => { timers.set(++id, callback); return id; };
  globalThis.clearTimeout = key => timers.delete(key);
  const external = new AbortController(); const listeners = new Set();
  const add = external.signal.addEventListener.bind(external.signal), remove = external.signal.removeEventListener.bind(external.signal);
  external.signal.addEventListener = (type, listener, options) => { if (type === "abort") listeners.add(listener); add(type, listener, options); };
  external.signal.removeEventListener = (type, listener, options) => { if (type === "abort") listeners.delete(listener); remove(type, listener, options); };
  try { await action({ external, listeners, timers, expire: () => [...timers.values()].forEach(callback => callback()) }); }
  finally { Object.assign(globalThis, original); }
}
for (const [name, call] of [["GET", opts => apiGet("/api/test", opts)], ["POST", opts => apiPost("/api/test", {}, opts)]]) {
  test(`${name}: headers do not end the JSON timeout; successful body cleans resources`, () => harness(async ({ external, listeners, timers }) => {
    const body = deferred(); globalThis.fetch = async () => response(body.promise);
    const pending = call({ signal: external.signal }); await flush();
    assert.equal(timers.size, 1); assert.equal(listeners.size, 1);
    body.resolve({ ok: true }); assert.deepEqual(await pending, { ok: true });
    assert.equal(timers.size, 0); assert.equal(listeners.size, 0);
  }));
  test(`${name}: timeout rejects an uncooperative body and cleans listeners`, () => harness(async ({ external, listeners, timers, expire }) => {
    globalThis.fetch = async () => response(new Promise(() => {}));
    const pending = call({ signal: external.signal }); await flush(); expire();
    await assert.rejects(pending, error => error.name === "TimeoutError");
    assert.equal(timers.size, 0); assert.equal(listeners.size, 0);
  }));
  test(`${name}: external cancellation during JSON reading propagates`, () => harness(async ({ external, listeners, timers }) => {
    globalThis.fetch = async () => response(new Promise(() => {}));
    const pending = call({ signal: external.signal }); await flush(); external.abort();
    await assert.rejects(pending, error => error.name === "AbortError");
    assert.equal(timers.size, 0); assert.equal(listeners.size, 0);
  }));
  for (const status of [429, 500]) test(`${name}: HTTP ${status} preserves its status and cleans resources`, () => harness(async ({ external, listeners, timers }) => {
    globalThis.fetch = async () => response(Promise.resolve({ title: "unavailable" }), status);
    await assert.rejects(call({ signal: external.signal }), error => error.status === status);
    assert.equal(timers.size, 0); assert.equal(listeners.size, 0);
  }));
  test(`${name}: invalid JSON propagates and cleans resources`, () => harness(async ({ external, listeners, timers }) => {
    globalThis.fetch = async () => ({ ...response(null), json: async () => { throw new SyntaxError("invalid JSON"); } });
    await assert.rejects(call({ signal: external.signal }), SyntaxError);
    assert.equal(timers.size, 0); assert.equal(listeners.size, 0);
  }));
}
test("error response body reads are also bounded", () => harness(async ({ external, listeners, timers, expire }) => {
  globalThis.fetch = async () => response(new Promise(() => {}), 500);
  const pending = apiGet("/api/test", { signal: external.signal }); await flush(); expire();
  await assert.rejects(pending, error => error.name === "TimeoutError");
  assert.equal(timers.size, 0); assert.equal(listeners.size, 0);
}));
test("repeated calls with a shared signal never accumulate listeners; pre-aborted reason survives", () => harness(async ({ external, listeners, timers }) => {
  globalThis.fetch = async () => response(Promise.resolve({ ok: true }));
  for (let i = 0; i < 40; i++) { await apiGet("/api/test", { signal: external.signal }); assert.equal(listeners.size, 0); assert.equal(timers.size, 0); }
  const reason = new Error("voluntary cancellation"); external.abort(reason);
  await assert.rejects(apiGet("/api/test", { signal: external.signal }), error => error === reason);
  assert.equal(listeners.size, 0); assert.equal(timers.size, 0);
}));
