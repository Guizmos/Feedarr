import test from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { openDownloadPath, downloadErrorMessage } from "./downloadPath.js";

function environment(t, respond, prepare = () => new Response(null, { status: 204 })) {
  const originals = Object.fromEntries(["window", "document", "fetch", "setTimeout", "clearTimeout"].map((key) => [key, globalThis[key]]));
  const frames = [];
  const lookups = [];
  const errors = [];
  const preparations = [];
  globalThis.window = { location: { origin: "https://feedarr.example" } };
  globalThis.document = {
    documentElement: { getAttribute: () => "fr-FR" },
    createElement(type) {
      assert.equal(type, "iframe");
      return { style: {}, setAttribute() {}, remove() { this.removed = true; } };
    },
    body: { appendChild(frame) { frames.push(frame); } },
  };
  globalThis.fetch = async (path, init) => {
    if (path === "/api/releases/7/download-session") {
      preparations.push({ path, init });
      assert.equal(init.method, "POST");
      assert.equal(init.credentials, "include");
      assert.equal(init.headers["X-Feedarr-Request"], "1");
      return prepare();
    }
    lookups.push({ path, init });
    assert.match(path, /^\/api\/releases\/7\/download-status\/[\da-f-]+$/);
    return respond(path, init);
  };
  globalThis.setTimeout = (fn, delay) => { if (delay === 1000) queueMicrotask(fn); return 1; };
  globalThis.clearTimeout = () => {};
  t.after(() => { for (const [key, value] of Object.entries(originals)) globalThis[key] = value; });
  return { frames, lookups, preparations, errors, onError: (message) => errors.push(message) };
}
const json = (payload, status = 200) => new Response(JSON.stringify(payload), {
  status, headers: { "content-type": "application/json" },
});

test("public download keeps one native request, credentials and no torrent fetch/blob", async (t) => {
  let calls = 0;
  const env = environment(t, () => json(++calls === 1 ? { state: "pending" } : { state: "completed", statusCode: 200 }));
  assert.equal(await openDownloadPath("/api/releases/7/download", env), true);
  assert.equal(env.frames.length, 1);
  assert.equal(env.lookups.length, 2);
  assert.match(env.frames[0].src, /^https:\/\/feedarr.example\/api\/releases\/7\/download\?downloadAttempt=/);
  assert.equal(env.frames[0].onload, undefined);
  assert.equal(env.frames[0].removed, true);
  assert.ok(env.lookups.every((lookup) => lookup.init.credentials === "include"));
  assert.deepEqual(env.errors, []); // no fabricated success notification either
});

for (const status of [400, 403, 404, 500, 502]) {
  test(`HTTP ${status} is visible, sanitized and never retried`, async (t) => {
    const env = environment(t, () => json({ state: "failed", statusCode: status,
      detail: "http://user:password@private/download?apikey=secret" }));
    assert.equal(await openDownloadPath("/api/releases/7/download", env), false);
    assert.equal(env.frames.length, 1);
    assert.equal(env.lookups.length, 1);
    assert.equal(env.errors.length, 1);
    assert.doesNotMatch(env.errors[0], /secret|password|private\/download/);
  });
}

test("private torrent refusal explicitly explains that RSS trust does not permit downloads", async (t) => {
  const env = environment(t, () => json({ state: "failed", statusCode: 400, code: "ssrf_blocked" }));
  assert.equal(await openDownloadPath("/api/releases/7/download", env), false);
  assert.match(env.errors[0], /SSRF.*privée.*RSS/);
  assert.equal(env.frames.length, 1);
});

test("authentication failure in the diagnostic API is visible without bypass or retry", async (t) => {
  const env = environment(t, () => json({ title: "unauthorized" }, 401));
  assert.equal(await openDownloadPath("/api/releases/7/download", env), false);
  assert.match(env.errors[0], /Authentification requise/);
  assert.equal(env.lookups.length, 1);
});

test("network failure in status tracking is not confused with a confirmed download failure", async (t) => {
  const env = environment(t, () => { throw new TypeError("network error with secret credentials"); });
  assert.equal(await openDownloadPath("/api/releases/7/download", env), false);
  assert.match(env.errors[0], /Impossible de vérifier.*Vérifiez les téléchargements/);
  assert.doesNotMatch(env.errors[0], /secret/);
  assert.equal(env.frames[0].removed, undefined); // do not interrupt a potentially running transfer
});

test("magnet keeps the same native navigation and never reaches fetch as a magnet URL", async (t) => {
  const env = environment(t, () => json({ state: "completed", statusCode: 302 }));
  assert.equal(await openDownloadPath("/api/releases/7/download", env), true);
  assert.equal(env.frames.length, 1);
  assert.equal(env.lookups.length, 1);
  assert.deepEqual(env.errors, []);
});

test("arbitrary paths are refused without creating requests", async (t) => {
  const env = environment(t, () => { throw new Error("must not run"); });
  for (const path of ["https://private/api", "/api/other", "/api/releases/7/download?url=private"]) {
    assert.equal(await openDownloadPath(path, env), false);
  }
  assert.equal(env.frames.length, 0);
  assert.equal(env.lookups.length, 0);
});

test("download messages support English and French and never use remote error text", (t) => {
  assert.match(downloadErrorMessage(502), /serveur|fournisseur/);
  assert.match(downloadErrorMessage(502, "network_error"), /réseau/);
  const previous = globalThis.document;
  t.after(() => { globalThis.document = previous; });
  globalThis.document = { documentElement: { getAttribute: () => "en-US" } };
  assert.match(downloadErrorMessage(502), /server or provider error/);
  assert.match(downloadErrorMessage(400, "ssrf_blocked"), /SSRF.*private/);
});

test("Library, Top and release details retain the same single-download error path", () => {
  const library = readFileSync(new URL("../pages/library/Library.jsx", import.meta.url), "utf8");
  const top = readFileSync(new URL("../pages/TopReleases.jsx", import.meta.url), "utf8");
  const modal = readFileSync(new URL("../ui/ReleaseModal.jsx", import.meta.url), "utf8");
  for (const page of [library, top]) {
    assert.equal((page.match(/openDownloadPath\(path,/g) || []).length, 1);
    assert.match(page, /onDownload=\{download\}/);
    assert.match(page, /onError: \(message\) => \{\s*setErr\(message\);\s*setSelectedItem\(null\)/);
  }
  assert.match(modal, /onClick=\{\(\) => onDownload\?\.\(item\)\}/);
});

for (const status of [401, 403]) {
  test(`session preparation HTTP ${status} never starts a native download or anonymous session retry`, async (t) => {
    const env = environment(t, () => { throw new Error("lookup must not run"); },
      () => json({ code: status === 403 ? "download_session_invalid" : undefined }, status));
    assert.equal(await openDownloadPath("/api/releases/7/download", env), false);
    assert.equal(env.preparations.length, 1);
    assert.equal(env.frames.length, 0);
    assert.equal(env.lookups.length, 0);
    assert.equal(env.errors.length, 1);
  });
}

test("concurrent downloads share session-cookie preparation but keep distinct native attempts", async (t) => {
  let release;
  const prepared = new Promise((resolve) => { release = resolve; });
  const env = environment(t, () => json({ state: "completed", statusCode: 200 }),
    () => prepared.then(() => new Response(null, { status: 204 })));
  const first = openDownloadPath("/api/releases/7/download", env);
  const second = openDownloadPath("/api/releases/7/download", env);
  assert.equal(env.frames.length, 0);
  assert.equal(env.preparations.length, 1);
  release();
  assert.deepEqual(await Promise.all([first, second]), [true, true]);
  assert.equal(env.frames.length, 2);
  assert.notEqual(env.frames[0].src, env.frames[1].src);
  assert.equal(env.preparations.length, 1);
});

test("the initial diagnostic lookup race never retries the native download", async (t) => {
  let lookups = 0;
  const env = environment(t, () => ++lookups === 1
    ? json({ code: "download_status_unavailable" }, 404)
    : json({ state: "completed", statusCode: 200 }));
  assert.equal(await openDownloadPath("/api/releases/7/download", env), true);
  assert.equal(env.frames.length, 1);
  assert.equal(env.preparations.length, 1);
  assert.equal(env.lookups.length, 2);
  assert.deepEqual(env.errors, []);
});

for (const origin of ["http://10.10.1.100:8888", "https://feedarr.example"]) {
  test(`download uses a cryptographic UUID v4 fallback without randomUUID at ${origin}`, async (t) => {
    const env = environment(t, () => json({ state: "completed", statusCode: 200 }));
    globalThis.window.location.origin = origin;
    let randomCalls = 0;
    const descriptor = Object.getOwnPropertyDescriptor(globalThis.crypto, "randomUUID");
    Object.defineProperty(globalThis.crypto, "randomUUID", { configurable: true, value: undefined });
    t.after(() => {
      if (descriptor) Object.defineProperty(globalThis.crypto, "randomUUID", descriptor);
      else delete globalThis.crypto.randomUUID;
    });
    t.mock.method(globalThis.crypto, "getRandomValues", (bytes) => {
      randomCalls++;
      assert.equal(bytes.length, 16);
      bytes.fill(255);
      return bytes;
    });
    assert.equal(await openDownloadPath("/api/releases/7/download", env), true);
    const url = new URL(env.frames[0].src);
    const attempt = url.searchParams.get("downloadAttempt");
    assert.equal(url.origin, origin);
    assert.equal(attempt, "ffffffff-ffff-4fff-bfff-ffffffffffff");
    assert.match(attempt, /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/);
    assert.equal(randomCalls, 1);
    assert.equal(env.frames.length, 1);
    assert.equal(env.preparations.length, 1);
    assert.equal(env.lookups[0].path, `/api/releases/7/download-status/${attempt}`);
    assert.deepEqual(env.errors, []);
  });
}

test("HTTPS keeps randomUUID when available without using the fallback", async (t) => {
  const env = environment(t, () => json({ state: "completed", statusCode: 200 }));
  const attempt = "12345678-1234-4123-8123-123456789abc";
  const native = t.mock.method(globalThis.crypto, "randomUUID", () => attempt);
  t.mock.method(globalThis.crypto, "getRandomValues", () => { throw new Error("fallback must not run"); });
  assert.equal(await openDownloadPath("/api/releases/7/download", env), true);
  assert.equal(native.mock.callCount(), 1);
  assert.equal(new URL(env.frames[0].src).searchParams.get("downloadAttempt"), attempt);
});
