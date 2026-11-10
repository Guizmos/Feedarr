import test from "node:test";
import assert from "node:assert/strict";
import { readCachedUpdate, UPDATE_CACHE_KEY, UPDATE_LAST_CHECK_TS_KEY } from "../badgeStorage.js";

test("old empty update cache is invalidated so the beta metadata fix is fetched immediately", () => {
  const previous = globalThis.window;
  const values = new Map([[UPDATE_CACHE_KEY, JSON.stringify({ enabled: true, latestRelease: null, releases: [] })], [UPDATE_LAST_CHECK_TS_KEY, String(Date.now())]]);
  globalThis.window = { localStorage: { getItem: key => values.get(key), removeItem: key => values.delete(key) } };
  try {
    assert.equal(readCachedUpdate(), null);
    assert.equal(values.has(UPDATE_LAST_CHECK_TS_KEY), false);
    values.set(UPDATE_CACHE_KEY, JSON.stringify({ enabled: true, latestRelease: null, releases: [], error: "GitHub inaccessible" }));
    assert.equal(readCachedUpdate().error, "GitHub inaccessible");
    values.set(UPDATE_CACHE_KEY, JSON.stringify({ enabled: true, latestRelease: null, releases: [], error: null }));
    assert.equal(readCachedUpdate().error, null);
  } finally { globalThis.window = previous; }
});
