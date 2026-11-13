import test from "node:test";
import assert from "node:assert/strict";
import { createSessionFetch, getSessionSnapshot, lockSession, refreshSession, login, logout, safeDestination, sessionRoute } from "./session.js";

const strict = { authRequired: true, authConfigured: true, authenticated: false, setupCompleted: true, webSessionAvailable: true };
const json = (body, status = 200, headers = {}) => new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json", ...headers } });

test("routing depends on initial account and authentication, never onboarding", () => {
  assert.equal(sessionRoute(null), "checking");
  assert.equal(sessionRoute(strict), "login");
  assert.equal(sessionRoute({ ...strict, initialAdminRequired: true }), "create-admin");
  assert.equal(sessionRoute({ ...strict, authenticated: true, setupCompleted: false }), "app");
  assert.equal(sessionRoute({ ...strict, authRequired: false, setupCompleted: false }), "app");
});

test("return destination rejects external URLs and redirect loops", () => {
  for (const path of ["https://evil.test", "//evil.test", "/\\evil.test", "/login", "/setup", "/login/again", "/library\n"]) assert.equal(safeDestination(path), "/library");
  assert.equal(safeDestination("/library?q=title#results"), "/library?q=title#results");
});

test("login sends rememberMe as JSON without Basic and retains only returned session", async () => {
  const original = globalThis.fetch;
  let captured;
  try {
    lockSession();
    globalThis.fetch = async (url, init) => { captured = { url, ...init }; return json({ ...strict, authenticated: true, csrfToken: "csrf-test" }); };
    await login("demo", "secret-test", true);
    assert.deepEqual(JSON.parse(captured.body), { username: "demo", password: "secret-test", rememberMe: true });
    assert.equal(captured.headers.Authorization, "");
    assert.equal(captured.credentials, "include");
    assert.equal(captured.cache, "no-store");
    assert.equal(getSessionSnapshot().state.csrfToken, "csrf-test");
    assert.doesNotMatch(JSON.stringify(getSessionSnapshot()), /secret-test|demo/);
  } finally { globalThis.fetch = original; lockSession(); }
});

test("login handles 401, 403 and 429 without changing anonymous state", async () => {
  const original = globalThis.fetch;
  try {
    for (const status of [401, 403, 429]) {
      lockSession();
      globalThis.fetch = async () => json({ code: "denied" }, status, { "Retry-After": "30" });
      await assert.rejects(login("demo", "incorrect", false), (error) => error.status === status && error.retryAfter === "30");
      assert.equal(getSessionSnapshot().state, null);
    }
  } finally { globalThis.fetch = original; lockSession(); }
});

test("network failure is an unavailable session, never a false expired-session verdict", async () => {
  const original = globalThis.fetch;
  try {
    lockSession();
    globalThis.fetch = async () => { throw new TypeError("offline"); };
    await assert.rejects(refreshSession(), /offline/);
    assert.equal(getSessionSnapshot().phase, "error");
    assert.equal(getSessionSnapshot().state, null);
  } finally { globalThis.fetch = original; lockSession(); }
});

test("all same-origin mutations receive CSRF, including security and legacy direct fetch", async () => {
  const original = globalThis.fetch;
  try {
    lockSession();
    globalThis.fetch = async () => json({ ...strict, authenticated: true, csrfToken: "bound-csrf" });
    await refreshSession();
    const calls = [];
    const transport = createSessionFetch(async (url, init) => { calls.push({ url, init }); return json({}); }, "https://feedarr.test", "");
    for (const path of ["/api/settings/security", "/api/settings/general", "/api/configuration-backups/prepare", "/api/sources"]) {
      await transport(path, { method: "PUT", headers: { "X-Feedarr-Request": "1" } });
      const headers = calls.at(-1).init.headers;
      assert.equal(headers.get("X-Feedarr-CSRF"), "bound-csrf");
      assert.equal(headers.get("X-Feedarr-Web"), "1");
      assert.equal(headers.get("Authorization"), "");
      // Security saves cause a policy recheck. Restore a session for the next assertion.
      await refreshSession();
    }
    await transport("/api/sources", { headers: { Accept: "application/json" } });
    assert.equal(calls.at(-1).init.headers.get("X-Feedarr-CSRF"), null);
  } finally { globalThis.fetch = original; lockSession(); }
});

test("CSRF never leaks to external requests or explicit Basic callers", async () => {
  const calls = [];
  const transport = createSessionFetch(async (url, init) => { calls.push({ url, init }); return json({}); }, "https://feedarr.test", "/feedarr");
  const external = { method: "POST", headers: { Accept: "application/json" } };
  await transport("https://other.test/feedarr/api/test", external);
  assert.equal(calls.at(-1).init, external);
  const basic = { headers: { Authorization: "Basic explicit" } };
  await transport("/feedarr/api/sources", basic);
  assert.equal(calls.at(-1).init, basic);
  await transport("/assets/public.js", external);
  assert.equal(calls.at(-1).init, external);
});

test("403 and 429 do not automatically sign out; 401 rechecks server state", async () => {
  const original = globalThis.fetch;
  try {
    lockSession();
    globalThis.fetch = async () => json({ ...strict, authenticated: true, csrfToken: "csrf" });
    await refreshSession();
    for (const status of [403, 429]) {
      const transport = createSessionFetch(async () => json({}, status), "https://feedarr.test", "");
      assert.equal((await transport("/api/sources")).status, status);
      assert.equal(getSessionSnapshot().state.authenticated, true);
    }
    globalThis.fetch = async () => json(strict);
    const transport = createSessionFetch(async () => json({}, 401), "https://feedarr.test", "");
    await transport("/api/sources");
    await refreshSession();
    assert.equal(sessionRoute(getSessionSnapshot().state), "login");
  } finally { globalThis.fetch = original; lockSession(); }
});

test("logout sends session CSRF, clears private memory and replaces browser location", async () => {
  const original = globalThis.fetch;
  const originalWindow = globalThis.window;
  let destination, captured;
  try {
    lockSession();
    globalThis.fetch = async () => json({ ...strict, authenticated: true, csrfToken: "csrf-logout" });
    await refreshSession();
    globalThis.window = { location: { replace: (path) => { destination = path; } } };
    globalThis.fetch = async (_url, init) => { captured = init; return json({ authenticated: false }); };
    await logout();
    assert.equal(captured.headers["X-Feedarr-CSRF"], "csrf-logout");
    assert.equal(getSessionSnapshot().state, null);
    assert.equal(destination, "/login");
  } finally { globalThis.fetch = original; globalThis.window = originalWindow; lockSession(); }
});

test("a late private response cannot repopulate state after logout or invalidation", async () => {
  let finish;
  const transport = createSessionFetch(() => new Promise((resolve) => { finish = resolve; }), "https://feedarr.test", "");
  const request = transport("/api/feed/releases");
  lockSession();
  finish(json({ items: ["private"] }));
  await assert.rejects(request, { name: "AbortError" });
});

test("setup-related 403 rechecks policy without treating ordinary forbidden errors as expiry", async () => {
  const original = globalThis.fetch;
  try {
    lockSession();
    globalThis.fetch = async () => json({ ...strict, authenticated: true });
    await refreshSession();
    globalThis.fetch = async () => json({ ...strict, setupCompleted: false });
    const transport = createSessionFetch(async () => json({ error: "setup_required" }, 403), "https://feedarr.test", "");
    await transport("/api/feed/releases");
    await refreshSession();
    assert.equal(getSessionSnapshot().state.setupCompleted, false);
  } finally { globalThis.fetch = original; lockSession(); }
});

test("page hiding masks and preserves private state and BFCache restoration requires a server recheck", async () => {
  const originalFetch = globalThis.fetch, originalWindow = globalThis.window, originalDocument = globalThis.document;
  const events = new Map(), classes = new Set();
  const doc = { visibilityState: "visible", documentElement: { classList: { add: (name) => classes.add(name), remove: (name) => classes.delete(name) } }, addEventListener: (name, fn) => events.set(name, fn) };
  try {
    const { installSessionTransport } = await import("./session.js");
    globalThis.document = doc;
    globalThis.window = { location: { origin: "https://feedarr.test" }, fetch: async () => json({ ...strict, authenticated: true }), addEventListener: (name, fn) => events.set(name, fn) };
    installSessionTransport();
    globalThis.fetch = window.fetch;
    lockSession(); await refreshSession();
    assert.equal(getSessionSnapshot().state.authenticated, true);
    events.get("pagehide")();
    assert.equal(getSessionSnapshot().state.authenticated, true);
    assert.equal(classes.has("auth-locked"), true);
    events.get("pageshow")({ persisted: true });
    await refreshSession();
    await Promise.resolve();
    assert.equal(getSessionSnapshot().phase, "ready");
    assert.equal(classes.has("auth-locked"), false);
    doc.visibilityState = "hidden";
    events.get("visibilitychange")();
    assert.equal(getSessionSnapshot().state.authenticated, true);
  } finally { globalThis.fetch = originalFetch; globalThis.window = originalWindow; globalThis.document = originalDocument; lockSession(); }
});

test("server errors and inconsistent authorization stay in error/retry instead of remount loops", async () => {
  const original = globalThis.fetch;
  try {
    lockSession();
    globalThis.fetch = async () => json({}, 500);
    await assert.rejects(refreshSession());
    assert.equal(getSessionSnapshot().phase, "error");
    assert.equal(getSessionSnapshot().state, null);
    globalThis.fetch = async () => json({ ...strict, authenticated: true });
    await refreshSession();
    const transport = createSessionFetch(async () => json({}, 401), "https://feedarr.test", "");
    await transport("/api/sources");
    await refreshSession().catch(() => {});
    assert.equal(getSessionSnapshot().phase, "error");
    assert.equal(getSessionSnapshot().error.message, "inconsistent_authorization_state");
  } finally { globalThis.fetch = original; lockSession(); }
});

test("session deadline bounds an uncooperative fetch AND response body and allows retry", async () => {
  const originals = { fetch: globalThis.fetch, setTimeout: globalThis.setTimeout, clearTimeout: globalThis.clearTimeout };
  const timers = new Map(); let id = 0;
  try {
    globalThis.setTimeout = fn => { timers.set(++id, fn); return id; };
    globalThis.clearTimeout = key => timers.delete(key);
    for (const phase of ["fetch", "body"]) {
      lockSession();
      globalThis.fetch = () => phase === "fetch" ? new Promise(() => {}) : Promise.resolve({ ok: true, json: () => new Promise(() => {}) });
      const pending = refreshSession();
      assert.equal(refreshSession(), pending); // same request, not just equivalent results
      for (let i = 0; i < 8; i++) await Promise.resolve();
      for (const expire of [...timers.values()]) expire();
      await assert.rejects(pending, { name: "TimeoutError" });
      assert.equal(getSessionSnapshot().phase, "error"); assert.equal(timers.size, 0);
      globalThis.fetch = async () => json({ ...strict, setupCompleted: false });
      await refreshSession(); assert.equal(getSessionSnapshot().phase, "ready");
    }
  } finally { lockSession(); Object.assign(globalThis, originals); }
});

test("login invalidates an overlapping session check; late data cannot occupy pending forever", async () => {
  const original = globalThis.fetch; let finish;
  try {
    lockSession();
    globalThis.fetch = url => url.endsWith("session") ? new Promise(resolve => { finish = resolve; }) : Promise.resolve(json({ ...strict, authenticated: true, setupCompleted: false }));
    const old = refreshSession(); const rejected = assert.rejects(old, { name: "AbortError" });
    await Promise.resolve(); await login("admin", "fixture", false); await rejected;
    finish(json(strict));
    globalThis.fetch = async () => json({ ...strict, authenticated: true, setupCompleted: false });
    await refreshSession(); assert.equal(getSessionSnapshot().state.authenticated, true);
  } finally { globalThis.fetch = original; lockSession(); }
});

test("login finishing after private state was locked cannot reopen it", async () => {
  const original = globalThis.fetch; let finish;
  try {
    lockSession(); globalThis.fetch = () => new Promise(resolve => { finish = resolve; });
    const pending = login("admin", "fixture", false); await Promise.resolve();
    lockSession(); finish(json({ ...strict, authenticated: true }));
    await assert.rejects(pending, { name: "AbortError" }); assert.equal(getSessionSnapshot().state, null);
  } finally { globalThis.fetch = original; lockSession(); }
});
