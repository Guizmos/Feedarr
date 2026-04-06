import test from "node:test";
import assert from "node:assert/strict";
import { canUseBadgeSseEnvironment } from "../useBadgeSse.js";

test("canUseBadgeSseEnvironment returns false without window", () => {
  const ok = canUseBadgeSseEnvironment(undefined, function EventSourceMock() {});
  assert.equal(ok, false);
});

test("canUseBadgeSseEnvironment returns false without EventSource", () => {
  const ok = canUseBadgeSseEnvironment({}, undefined);
  assert.equal(ok, false);
});

test("canUseBadgeSseEnvironment returns true when window and EventSource exist", () => {
  const ok = canUseBadgeSseEnvironment({}, function EventSourceMock() {});
  assert.equal(ok, true);
});
