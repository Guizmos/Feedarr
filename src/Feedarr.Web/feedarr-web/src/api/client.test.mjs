import test from "node:test";
import assert from "node:assert/strict";
import { apiGet, apiPost, resolveApiUrl } from "./client.js";

test("resolveApiUrl keeps absolute, blob and data URLs unchanged", () => {
  assert.equal(resolveApiUrl("https://example.com/x"), "https://example.com/x");
  assert.equal(resolveApiUrl("blob:abc"), "blob:abc");
  assert.equal(resolveApiUrl("data:text/plain,hello"), "data:text/plain,hello");
});

test("apiGet uses normalized error from json payload", async () => {
  const originalFetch = globalThis.fetch;
  globalThis.fetch = async () =>
    new Response(
      JSON.stringify({ title: "upstream unavailable", detail: "debug info should stay server-side" }),
      {
        status: 502,
        headers: { "content-type": "application/json" },
      }
    );

  try {
    await assert.rejects(() => apiGet("/api/system/status"), /upstream unavailable/);
  } finally {
    globalThis.fetch = originalFetch;
  }
});

test("apiPost returns null for 204 and non-json responses", async () => {
  const originalFetch = globalThis.fetch;
  const responses = [
    new Response(null, { status: 204 }),
    new Response("ok", { status: 200, headers: { "content-type": "text/plain" } }),
  ];
  globalThis.fetch = async () => responses.shift();

  try {
    const first = await apiPost("/api/maintenance/cleanup-posters", { dryRun: true });
    const second = await apiPost("/api/system/backups/purge", {});
    assert.equal(first, null);
    assert.equal(second, null);
  } finally {
    globalThis.fetch = originalFetch;
  }
});

test("apiPost parses json when content-type is json", async () => {
  const originalFetch = globalThis.fetch;
  let capturedInit = null;
  globalThis.fetch = async (_, init) => {
    capturedInit = init;
    return new Response(JSON.stringify({ ok: true, count: 3 }), {
      status: 200,
      headers: { "content-type": "application/json; charset=utf-8" },
    });
  };

  try {
    const data = await apiPost("/api/sources/sync/all", {});
    assert.deepEqual(data, { ok: true, count: 3 });
    assert.equal(capturedInit?.headers?.["X-Feedarr-Request"], "1");
  } finally {
    globalThis.fetch = originalFetch;
  }
});

test("apiGet does not force anti-csrf header on safe methods", async () => {
  const originalFetch = globalThis.fetch;
  let capturedInit = null;
  globalThis.fetch = async (_, init) => {
    capturedInit = init;
    return new Response(JSON.stringify({ ok: true }), {
      status: 200,
      headers: { "content-type": "application/json" },
    });
  };

  try {
    await apiGet("/api/system/status");
    assert.equal(capturedInit?.headers?.["X-Feedarr-Request"], undefined);
  } finally {
    globalThis.fetch = originalFetch;
  }
});
