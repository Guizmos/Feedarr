import test from "node:test";
import assert from "node:assert/strict";
import {
  extractCategoryIds,
  parseCategoryIdsFromData,
  parseCategoryIdsFromMessage,
} from "./historyCategories.js";

test("parseCategoryIdsFromData reads categoryIds and nested categories", () => {
  const data = {
    categoryIds: [2000, "5000", 5000],
    categories: [{ id: 2040 }, { id: "5070" }, { id: null }],
  };

  assert.deepEqual(parseCategoryIdsFromData(data), [2000, 5000, 2040, 5070]);
});

test("parseCategoryIdsFromMessage keeps legacy cats= parser", () => {
  const message = "Sync debug: fetched=10, mode=rss_only, cats=2040=5, 5070=3 missing=2000";
  assert.deepEqual(parseCategoryIdsFromMessage(message), [2040, 5070]);
});

test("extractCategoryIds prioritizes dataJson over legacy message", () => {
  const entry = {
    message: "Sync debug: fetched=8, mode=rss_only, cats=2040=4,5070=2",
  };
  const data = {
    categoryIds: [2000],
  };

  assert.deepEqual(extractCategoryIds(entry, data), [2000]);
});

