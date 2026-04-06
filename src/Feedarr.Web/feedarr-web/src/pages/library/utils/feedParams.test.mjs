import test from "node:test";
import assert from "node:assert/strict";
import { buildFeedParams } from "./feedParams.js";

test("buildFeedParams always includes limit", () => {
  const params = buildFeedParams("", "", 150);
  assert.equal(params.get("limit"), "150");
});

test("buildFeedParams trims query and includes seen flag", () => {
  const params = buildFeedParams("  matrix  ", "seen", 100);
  assert.equal(params.get("q"), "matrix");
  assert.equal(params.get("seen"), "seen");
  assert.equal(params.get("limit"), "100");
});

test("buildFeedParams omits empty query and empty seen", () => {
  const params = buildFeedParams("   ", "", 50);
  assert.equal(params.has("q"), false);
  assert.equal(params.has("seen"), false);
  assert.equal(params.get("limit"), "50");
});
