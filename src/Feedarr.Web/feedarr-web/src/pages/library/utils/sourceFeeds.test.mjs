import test from "node:test";
import assert from "node:assert/strict";
import { loadSourceFeeds, sourceFailureMessage } from "./sourceFeeds.js";
const sources = [{ id: 1 }, { sourceId: 2 }];
const params = new URLSearchParams({ q: "test query", seen: "unseen", limit: "300" });
const options = fetchFeed => ({ fetchFeed, signal: new AbortController().signal });
test("all sources succeed with unchanged filters and source attribution", async () => {
  const urls = [];
  const result = await loadSourceFeeds(sources, params, options(async url => { urls.push(url); return [{ id: url.includes('/1?') ? 10 : 20 }]; }));
  assert.equal(result.outcome, "success"); assert.deepEqual(result.failedSourceIds, []);
  assert.deepEqual(result.items.map(item => item.sourceId), [1, 2]);
  urls.forEach(url => assert.equal(url.split('?')[1], params.toString()));
});
for (const failure of [Object.assign(new Error("secret=https://private/?token=secret"), { status: 429 }), Object.assign(new Error("private"), { status: 500 }), new DOMException("timeout", "TimeoutError")]) {
  test(`partial failure ${failure.status || failure.name} keeps available releases and safe source IDs`, async () => {
    const result = await loadSourceFeeds(sources, params, options(async url => { if (url.includes('/2?')) throw failure; return [{ id: 10, quality: "1080p" }]; }));
    assert.equal(result.outcome, "partial"); assert.deepEqual(result.items, [{ id: 10, quality: "1080p", sourceId: 1 }]);
    assert.deepEqual(result.failedSourceIds, [2]); assert.doesNotMatch(sourceFailureMessage(result.failedSourceIds), /secret|https|token/);
  });
}
test("all failures produce an error, never a successful empty library", async () => {
  const result = await loadSourceFeeds(sources, params, options(async () => { throw new TypeError("network"); }));
  assert.equal(result.outcome, "error"); assert.deepEqual(result.failedSourceIds, [1, 2]);
  assert.match(sourceFailureMessage(result.failedSourceIds, true), /toutes les sources ont échoué/);
});
test("successful empty feeds differ from failures; partial empty feeds still warn", async () => {
  assert.equal((await loadSourceFeeds(sources, params, options(async () => []))).outcome, "success");
  assert.equal((await loadSourceFeeds(sources, params, options(async url => { if (url.includes('/2?')) throw new Error(); return []; }))).outcome, "partial");
});
test("voluntary navigation cancellation produces no result to publish", async () => {
  const controller = new AbortController();
  await assert.rejects(loadSourceFeeds(sources, params, { signal: controller.signal, fetchFeed: async () => { controller.abort(); throw controller.signal.reason; } }), error => error.name === "AbortError");
});
test("obsolete responses arriving after a new search cannot be published", async () => {
  const old = new AbortController(); let finish;
  const obsolete = loadSourceFeeds([sources[0]], params, { signal: old.signal, fetchFeed: () => new Promise(resolve => { finish = resolve; }) });
  old.abort();
  const latest = await loadSourceFeeds(sources, params, options(async () => [{ id: "new" }]));
  finish([{ id: "old" }]); await assert.rejects(obsolete, error => error.name === "AbortError");
  assert.equal(latest.items[0].id, "new");
});
test("malformed feeds and identities are failures rather than false empty results", async () => {
  const result = await loadSourceFeeds([{ id: "https://secret/?token=x" }], params, options(async () => []));
  assert.equal(result.outcome, "error"); assert.deepEqual(result.failedSourceIds, [null]);
  assert.doesNotMatch(sourceFailureMessage(result.failedSourceIds, true), /secret|token|https/);
  assert.equal((await loadSourceFeeds(sources, params, options(async () => null))).outcome, "error");
});
