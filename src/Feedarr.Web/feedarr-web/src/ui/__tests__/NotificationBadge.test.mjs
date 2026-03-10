/**
 * Tests for NotificationBadge pure helper functions.
 *
 * Run: node --test src/ui/__tests__/NotificationBadge.test.mjs
 */
import test from "node:test";
import assert from "node:assert/strict";
import {
  shouldShowBadge,
  getBadgeLabel,
  buildBadgeClass,
} from "../notificationBadge.js";

// ---------------------------------------------------------------------------
// shouldShowBadge
// ---------------------------------------------------------------------------

test("shouldShowBadge: returns false for null", () => {
  assert.equal(shouldShowBadge(null), false);
});

test("shouldShowBadge: returns false for undefined", () => {
  assert.equal(shouldShowBadge(undefined), false);
});

test("shouldShowBadge: returns false for false", () => {
  assert.equal(shouldShowBadge(false), false);
});

test("shouldShowBadge: returns false for 0", () => {
  assert.equal(shouldShowBadge(0), false);
});

test("shouldShowBadge: returns true for positive number", () => {
  assert.equal(shouldShowBadge(1), true);
  assert.equal(shouldShowBadge(42), true);
});

test("shouldShowBadge: returns true for string 'warn'", () => {
  assert.equal(shouldShowBadge("warn"), true);
});

test("shouldShowBadge: returns true for non-zero string", () => {
  assert.equal(shouldShowBadge("3"), true);
});

// ---------------------------------------------------------------------------
// getBadgeLabel
// ---------------------------------------------------------------------------

test("getBadgeLabel: returns '!' for 'warn'", () => {
  assert.equal(getBadgeLabel("warn"), "!");
});

test("getBadgeLabel: passes through numeric value", () => {
  assert.equal(getBadgeLabel(5), 5);
});

test("getBadgeLabel: passes through string non-warn", () => {
  assert.equal(getBadgeLabel("3"), "3");
});

// ---------------------------------------------------------------------------
// buildBadgeClass
// ---------------------------------------------------------------------------

test("buildBadgeClass: returns baseClass alone when no tone and no extraClass", () => {
  assert.equal(buildBadgeClass("snav__badge", undefined, undefined), "snav__badge");
});

test("buildBadgeClass: appends tone modifier", () => {
  assert.equal(buildBadgeClass("snav__badge", "error", undefined), "snav__badge snav__badge--error");
});

test("buildBadgeClass: appends extraClass", () => {
  assert.equal(buildBadgeClass("snav__badge", undefined, "snav__badge--pulse"), "snav__badge snav__badge--pulse");
});

test("buildBadgeClass: appends both tone and extraClass", () => {
  assert.equal(
    buildBadgeClass("snav__badge", "warn", "snav__badge--pulse"),
    "snav__badge snav__badge--warn snav__badge--pulse"
  );
});

test("buildBadgeClass: works with subaction__badge base", () => {
  assert.equal(buildBadgeClass("subaction__badge", "info", undefined), "subaction__badge subaction__badge--info");
});

test("buildBadgeClass: skips tone when empty string", () => {
  assert.equal(buildBadgeClass("snav__badge", "", undefined), "snav__badge");
});

test("buildBadgeClass: skips extraClass when empty string", () => {
  assert.equal(buildBadgeClass("snav__badge", undefined, ""), "snav__badge");
});
