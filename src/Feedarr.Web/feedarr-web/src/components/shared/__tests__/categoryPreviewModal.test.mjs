/**
 * Unit tests for CategoryPreviewModal fetch logic.
 * Uses Node's built-in test runner (no framework needed).
 *
 * Run: node --test src/components/shared/__tests__/categoryPreviewModal.test.mjs
 */
import test from "node:test";
import assert from "node:assert/strict";

// ---------------------------------------------------------------------------
// Minimal stubs for the URL builder — mirrors the real logic
// ---------------------------------------------------------------------------
function buildPreviewUrl(sourceId, catId, limit = 20) {
  return `/api/sources/${sourceId}/category-preview?catId=${catId}&limit=${limit}`;
}

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

test("buildPreviewUrl constructs the correct endpoint", () => {
  assert.equal(
    buildPreviewUrl(3, 2040),
    "/api/sources/3/category-preview?catId=2040&limit=20"
  );
});

test("buildPreviewUrl respects custom limit", () => {
  assert.equal(
    buildPreviewUrl(7, 5070, 5),
    "/api/sources/7/category-preview?catId=5070&limit=5"
  );
});

test("empty API response yields empty items array", () => {
  // Simulate: fetch returns [], component sets items = []
  const data = [];
  const items = Array.isArray(data) ? data : [];
  assert.equal(items.length, 0);
});

test("non-array API response is safely coerced to empty array", () => {
  const data = null;
  const items = Array.isArray(data) ? data : [];
  assert.deepEqual(items, []);
});

test("items from API are passed through unchanged", () => {
  const data = [
    { publishedAtTs: 9000000, sourceName: "YGEGE", title: "Film 4K HDR", sizeBytes: 4294967296, categoryId: 2040, unifiedCategory: "Film", tmdbId: 123, tvdbId: null, seeders: 50 },
    { publishedAtTs: 8000000, sourceName: "YGEGE", title: "Serie S01E01", sizeBytes: 1073741824, categoryId: 5040, unifiedCategory: "Serie", tmdbId: null, tvdbId: 456, seeders: 10 },
  ];
  const items = Array.isArray(data) ? data : [];
  assert.equal(items.length, 2);
  assert.equal(items[0].title, "Film 4K HDR");
  assert.equal(items[0].tmdbId, 123);
  assert.equal(items[1].tvdbId, 456);
});

test("modal title is constructed correctly", () => {
  const catName = "Movies/HD";
  const catId = 2040;
  const title = `Aperçu : ${catName} (id ${catId}) — 20 derniers résultats`;
  assert.equal(title, "Aperçu : Movies/HD (id 2040) — 20 derniers résultats");
});

// ---------------------------------------------------------------------------
// Category column render logic (mirrors CategoryPreviewModal.jsx)
// ---------------------------------------------------------------------------
function renderCatLabel(item) {
  return item.categoryId
    ? `${item.categoryId}${item.resultCategoryName ? ` • ${item.resultCategoryName}` : ""}`
    : item.unifiedCategory || "—";
}

test("category column renders id + name when resultCategoryName is present", () => {
  const item = { categoryId: 100315, resultCategoryName: "FLAC", unifiedCategory: null };
  assert.equal(renderCatLabel(item), "100315 • FLAC");
});

test("category column renders id only when resultCategoryName is absent", () => {
  const item = { categoryId: 2040, resultCategoryName: null, unifiedCategory: null };
  assert.equal(renderCatLabel(item), "2040");
});

test("category column falls back to unifiedCategory when no categoryId", () => {
  const item = { categoryId: null, resultCategoryName: null, unifiedCategory: "Films" };
  assert.equal(renderCatLabel(item), "Films");
});

test("category column shows dash when no categoryId and no unifiedCategory", () => {
  const item = { categoryId: null, resultCategoryName: null, unifiedCategory: null };
  assert.equal(renderCatLabel(item), "—");
});

// ---------------------------------------------------------------------------
// Cat summary computation (mirrors CategoryPreviewModal.jsx useMemo)
// ---------------------------------------------------------------------------
function buildCatSummary(items) {
  const counts = {};
  for (const item of items) {
    const id = item.categoryId;
    if (!id) continue;
    counts[id] = {
      count: (counts[id]?.count || 0) + 1,
      name: counts[id]?.name || item.resultCategoryName || null,
    };
  }
  return Object.entries(counts).sort((a, b) => b[1].count - a[1].count);
}

test("cat summary aggregates by categoryId descending count", () => {
  const items = [
    { categoryId: 100314, resultCategoryName: "Musique" },
    { categoryId: 100315, resultCategoryName: "FLAC" },
    { categoryId: 100314, resultCategoryName: "Musique" },
    { categoryId: 100315, resultCategoryName: "FLAC" },
    { categoryId: 100315, resultCategoryName: "FLAC" },
  ];
  const summary = buildCatSummary(items);
  // 100315 appears 3 times, 100314 appears 2 times → sorted desc
  assert.equal(summary[0][0], "100315");
  assert.equal(summary[0][1].count, 3);
  assert.equal(summary[0][1].name, "FLAC");
  assert.equal(summary[1][0], "100314");
  assert.equal(summary[1][1].count, 2);
});

test("cat summary ignores items with no categoryId", () => {
  const items = [
    { categoryId: 2000, resultCategoryName: "Movies" },
    { categoryId: null, resultCategoryName: null },
  ];
  const summary = buildCatSummary(items);
  assert.equal(summary.length, 1);
  assert.equal(summary[0][0], "2000");
});
