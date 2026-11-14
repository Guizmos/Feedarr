import test from "node:test";
import assert from "node:assert/strict";
import { buildFeedParams, loadLibraryPage } from "./feedParams.js";

const catalog = Array.from({ length: 2405 }, (_, id) => ({ id: id + 1, sourceId: id % 2 + 1 }));
function server(calls) {
  return async url => {
    const p = new URL(url, "https://fixture.test").searchParams;
    calls.push(p);
    const offset = Number(p.get("cursor") || 0), limit = Number(p.get("limit"));
    const items = catalog.slice(offset, offset + limit);
    const end = offset + items.length;
    return { items, pagination: { hasMore: end < catalog.length, nextCursor: String(end) }, facets: offset ? null : { categories: ["films"], qualities: ["1080p"] } };
  };
}

test("all catalog filters and list ordering are sent before SQL pagination", () => {
  const p = buildFeedParams({ q: " Amélie ", sourceId: "2", seen: "0", categoryId: "films", quality: "1080p", maxAgeDays: "3", viewMode: "list", listSortBy: "downloads", listSortDir: "asc" }, 1_000_000_000);
  assert.deepEqual(Object.fromEntries(p), { sort: "grabs", direction: "asc", categorized: "true", q: "Amélie", seen: "0", sourceId: "2", unifiedCategory: "films", quality: "1080p", publishedAfter: "740800" });
  assert.equal(buildFeedParams({ viewMode: "missing", sortBy: "seeders" }).get("missingPoster"), "true");
  assert.equal(buildFeedParams({}).get("sort"), "date");
});

test("one display window is bounded, subsequent windows reach all 2405 rows without duplicates", async () => {
  const calls = [], ids = [];
  let cursor = null;
  do {
    const page = await loadLibraryPage(new URLSearchParams(), { cursor, limit: 500, fetchFeed: server(calls) });
    assert.ok(page.items.length <= 500);
    ids.push(...page.items.map(i => i.id));
    cursor = page.nextCursor;
  } while (cursor);
  assert.deepEqual(ids, catalog.map(i => i.id));
  assert.equal(new Set(ids).size, 2405);
  assert.ok(calls.every(p => Number(p.get("limit")) <= 200));
  const firstCalls = [];
  await loadLibraryPage(new URLSearchParams(), { limit: 100, fetchFeed: server(firstCalls) });
  assert.equal(firstCalls.length, 1, "a normal page does not drain the catalog");
});

test("sparse application filters scan past 500 and discard rejected pages", async () => {
  const calls = [];
  const page = await loadLibraryPage(new URLSearchParams(), {
    limit: 5, fetchFeed: server(calls),
    selectItems: async rows => rows.filter(i => i.id > 1200),
  });
  assert.deepEqual(page.items.map(i => i.id), [1201, 1202, 1203, 1204, 1205]);
  assert.ok(Number(page.nextCursor) >= 1205);
  assert.equal(page.items.length, 5);
  assert.ok(calls.every(p => Number(p.get("limit")) <= 200));
  assert.ok(calls.length <= 8, "sparse filters must not degenerate into one request per candidate");
});

test("failed, nonadvancing and cancelled requests never present a truncated success", async () => {
  await assert.rejects(loadLibraryPage(new URLSearchParams(), { fetchFeed: async () => { throw new Error("offline"); } }), /offline/);
  await assert.rejects(loadLibraryPage(new URLSearchParams(), { cursor: "same", fetchFeed: async () => ({ items: [], pagination: { hasMore: true, nextCursor: "same" } }) }), /Invalid/);
  const controller = new AbortController(); controller.abort();
  await assert.rejects(loadLibraryPage(new URLSearchParams(), { signal: controller.signal, fetchFeed: () => assert.fail("must not fetch") }), { name: "AbortError" });
});
