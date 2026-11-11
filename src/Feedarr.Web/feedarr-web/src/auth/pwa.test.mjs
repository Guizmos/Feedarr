import test from "node:test";
import assert from "node:assert/strict";
import vm from "node:vm";
import { readFileSync } from "node:fs";
const source = readFileSync(new URL("../../public/service-worker.js", import.meta.url), "utf8");
function worker({ prefix = "", offline = false, status = 200, privateResponse = false } = {}) {
  const handlers = new Map(), stores = new Map(), calls = [];
  let activated = false;
  const key = (input) => new URL(typeof input === "string" ? input : input.url, "https://feedarr.test").href;
  const caches = {
    async keys() { return [...stores.keys()]; },
    async delete(name) { return stores.delete(name); },
    async open(name) {
      if (!stores.has(name)) stores.set(name, new Map());
      return { async put(input, response) { stores.get(name).set(key(input), response); }, async match(input) { return stores.get(name).get(key(input)); } };
    },
  };
  const self = {
    location: new URL(`https://feedarr.test${prefix}/service-worker.js?v=build-test`),
    registration: { scope: `https://feedarr.test${prefix}/` },
    clients: { async claim() {} }, async skipWaiting() { activated = true; },
    addEventListener: (name, listener) => handlers.set(name, listener),
  };
  vm.runInNewContext(source, {
    self, caches, URL, Request, Response,
    fetch: async (input, init) => {
      calls.push({ input, init });
      if (offline) throw new TypeError("offline");
      const url = key(input);
      if (url.endsWith("pwa-assets.json")) return new Response(JSON.stringify(["assets/main-hash.js", "assets/style-hash.css"]));
      return new Response("public shell", { status, headers: { "X-Feedarr-Shell": "public-v1", ...(privateResponse ? { "Cache-Control": "private, no-store" } : {}) } });
    },
  });
  return { handlers, stores, calls, caches, get activated() { return activated; } };
}
async function lifecycle(w, name) {
  let pending;
  w.handlers.get(name)({ waitUntil(value) { pending = value; } });
  await pending;
}
function request(w, path, mode = "navigate") {
  let pending;
  w.handlers.get("fetch")({ request: { method: "GET", url: `https://feedarr.test${path}`, mode }, respondWith(value) { pending = value; } });
  return pending;
}

test("precache contains only public anonymous assets and preserves versioned icons", async () => {
  const w = worker();
  await lifecycle(w, "install");
  assert.equal(w.activated, true);
  const entries = [...w.stores.values()][0];
  assert.ok(entries.has("https://feedarr.test/index.html"));
  assert.ok(entries.has("https://feedarr.test/assets/main-hash.js"));
  assert.ok(entries.has("https://feedarr.test/icon-maskable-512.png?v=9d1d5be2bd5c"));
  for (const { input, init } of w.calls) {
    assert.equal(init.credentials, "omit");
    assert.equal(init.redirect, "error");
    assert.doesNotMatch(String(input), /\/api\/|\/login/);
  }
});

test("failed or private precache never activates silently", async () => {
  for (const options of [{ status: 401 }, { status: 403 }, { privateResponse: true }, { offline: true }]) {
    const w = worker(options);
    await assert.rejects(lifecycle(w, "install"));
    assert.equal(w.activated, false);
  }
});

test("activation removes only old Feedarr PWA caches", async () => {
  const w = worker();
  await w.caches.open("feedarr-pwa-v4"); await w.caches.open("feedarr-pwa-v5"); await w.caches.open("other-app");
  await lifecycle(w, "install"); await lifecycle(w, "activate");
  assert.deepEqual([...w.stores.keys()].sort(), ["feedarr-pwa-auth-v6-build-test", "other-app"]);
});

test("API responses are never intercepted, including scoped and root API URLs", () => {
  const w = worker({ prefix: "/feedarr" });
  for (const path of ["/api/feed/releases", "/feedarr/api/auth/session", "/feedarr/api/posters/1", "/feedarr/api"]) assert.equal(request(w, path), undefined);
  assert.equal(w.calls.length, 0);
});

test("offline navigation awaits the public index fallback, including a path prefix", async () => {
  const w = worker({ prefix: "/feedarr", offline: true });
  const cache = await w.caches.open("feedarr-pwa-auth-v6-build-test");
  await cache.put("/feedarr/index.html", new Response("safe cached shell"));
  const response = await request(w, "/feedarr/library");
  assert.equal(await response.text(), "safe cached shell");
  assert.equal(w.calls[0].init.credentials, "omit");
});

test("navigations and HTTP auth errors are never cached", async () => {
  for (const status of [200, 401, 403, 302]) {
    const w = worker({ status });
    const response = await request(w, "/login");
    assert.equal(response.status, status);
    assert.equal(w.stores.size, 0);
    await request(w, "/assets/main.js", "cors");
    const entries = [...w.stores.values()][0];
    assert.equal(entries.size, status === 200 ? 1 : 0);
  }
});

test("offline without a shell gives an explicit unavailable page", async () => {
  const w = worker({ offline: true });
  const response = await request(w, "/library");
  assert.equal(response.status, 503);
  assert.equal(response.headers.get("Cache-Control"), "no-store");
  assert.match(await response.text(), /Offline/);
});
