/**
 * Tests for pure badge mapper functions.
 *
 * Run: node --test src/badges/__tests__/badgeMappers.test.mjs
 */
import test from "node:test";
import assert from "node:assert/strict";
import {
  parseTs,
  computeReleasesBadgeValue,
  normalizeActivityTone,
  normalizeSystemTone,
  normalizeReleasesTone,
} from "../badgeMappers.js";

// ---------------------------------------------------------------------------
// parseTs
// ---------------------------------------------------------------------------

test("parseTs: returns 0 for null / undefined", () => {
  assert.equal(parseTs(null), 0);
  assert.equal(parseTs(undefined), 0);
});

test("parseTs: passes through finite numbers", () => {
  assert.equal(parseTs(0), 0);
  assert.equal(parseTs(1700000000), 1700000000);
  assert.equal(parseTs(-1), -1);
});

test("parseTs: returns 0 for NaN / Infinity", () => {
  assert.equal(parseTs(NaN), 0);
  assert.equal(parseTs(Infinity), 0);
  assert.equal(parseTs(-Infinity), 0);
});

test("parseTs: coerces numeric strings", () => {
  assert.equal(parseTs("1700000000"), 1700000000);
  assert.equal(parseTs("0"), 0);
});

test("parseTs: parses ISO date strings via Date.parse", () => {
  const iso = "2024-01-15T12:00:00.000Z";
  assert.equal(parseTs(iso), Date.parse(iso));
});

test("parseTs: returns 0 for unparseable strings", () => {
  assert.equal(parseTs("not-a-date"), 0);
  assert.equal(parseTs(""), 0);
});

// ---------------------------------------------------------------------------
// computeReleasesBadgeValue
// ---------------------------------------------------------------------------

test("computeReleasesBadgeValue: exact count > 0 wins", () => {
  assert.equal(
    computeReleasesBadgeValue({ hasExactUnseenCount: true, exactUnseenCount: 5, releasesDelta: 10 }),
    5
  );
});

test("computeReleasesBadgeValue: exact count 0 returns 0", () => {
  assert.equal(
    computeReleasesBadgeValue({ hasExactUnseenCount: true, exactUnseenCount: 0, releasesDelta: 10 }),
    0
  );
});

test("computeReleasesBadgeValue: falls back to delta when no exact count", () => {
  assert.equal(
    computeReleasesBadgeValue({ hasExactUnseenCount: false, exactUnseenCount: null, releasesDelta: 7 }),
    7
  );
});

test("computeReleasesBadgeValue: returns 0 when delta is 0", () => {
  assert.equal(
    computeReleasesBadgeValue({ hasExactUnseenCount: false, exactUnseenCount: null, releasesDelta: 0 }),
    0
  );
});

test("computeReleasesBadgeValue: returns 0 when delta is null", () => {
  assert.equal(
    computeReleasesBadgeValue({ hasExactUnseenCount: false, exactUnseenCount: null, releasesDelta: null }),
    0
  );
});

// ---------------------------------------------------------------------------
// normalizeActivityTone
// ---------------------------------------------------------------------------

test("normalizeActivityTone: 'error' and 'warn' pass through", () => {
  assert.equal(normalizeActivityTone("error"), "error");
  assert.equal(normalizeActivityTone("warn"), "warn");
});

test("normalizeActivityTone: anything else normalizes to 'info'", () => {
  assert.equal(normalizeActivityTone("info"), "info");
  assert.equal(normalizeActivityTone(""), "info");
  assert.equal(normalizeActivityTone(null), "info");
  assert.equal(normalizeActivityTone(undefined), "info");
  assert.equal(normalizeActivityTone("unknown"), "info");
});

test("normalizeActivityTone: case-insensitive", () => {
  assert.equal(normalizeActivityTone("ERROR"), "error");
  assert.equal(normalizeActivityTone("WARN"), "warn");
});

// ---------------------------------------------------------------------------
// normalizeSystemTone
// ---------------------------------------------------------------------------

test("normalizeSystemTone: 'warn' returns warn", () => {
  assert.equal(normalizeSystemTone("warn"), "warn");
});

test("normalizeSystemTone: 'error' returns error", () => {
  assert.equal(normalizeSystemTone("error"), "error");
});

test("normalizeSystemTone: null returns null (clear badge)", () => {
  assert.equal(normalizeSystemTone(null), null);
});

test("normalizeSystemTone: undefined (absent field) returns undefined (keep prev)", () => {
  assert.equal(normalizeSystemTone(undefined), undefined);
});

test("normalizeSystemTone: unknown value returns undefined (keep prev)", () => {
  assert.equal(normalizeSystemTone("something"), undefined);
  assert.equal(normalizeSystemTone(0), undefined);
});

// ---------------------------------------------------------------------------
// normalizeReleasesTone
// ---------------------------------------------------------------------------

test("normalizeReleasesTone: backend 'warn' takes priority", () => {
  assert.equal(
    normalizeReleasesTone({ backendToneRaw: "warn", hasExactUnseenCount: true, releasesDelta: 5, hasNewByTs: false }),
    "warn"
  );
});

test("normalizeReleasesTone: backend 'info' takes priority", () => {
  assert.equal(
    normalizeReleasesTone({ backendToneRaw: "info", hasExactUnseenCount: false, releasesDelta: 0, hasNewByTs: true }),
    "info"
  );
});

test("normalizeReleasesTone: fallback to warn when no count/delta but hasNewByTs", () => {
  assert.equal(
    normalizeReleasesTone({ backendToneRaw: "", hasExactUnseenCount: false, releasesDelta: null, hasNewByTs: true }),
    "warn"
  );
});

test("normalizeReleasesTone: fallback to info when hasExactUnseenCount", () => {
  assert.equal(
    normalizeReleasesTone({ backendToneRaw: "", hasExactUnseenCount: true, exactUnseenCount: 0, releasesDelta: null, hasNewByTs: true }),
    "info"
  );
});

test("normalizeReleasesTone: fallback to info when delta > 0 (even with hasNewByTs)", () => {
  assert.equal(
    normalizeReleasesTone({ backendToneRaw: "", hasExactUnseenCount: false, releasesDelta: 3, hasNewByTs: true }),
    "info"
  );
});

test("normalizeReleasesTone: fallback to info when none of the conditions are met", () => {
  assert.equal(
    normalizeReleasesTone({ backendToneRaw: "", hasExactUnseenCount: false, releasesDelta: 0, hasNewByTs: false }),
    "info"
  );
});

test("normalizeReleasesTone: backend tone is case-insensitive", () => {
  assert.equal(
    normalizeReleasesTone({ backendToneRaw: "WARN", hasExactUnseenCount: false, releasesDelta: 0, hasNewByTs: false }),
    "warn"
  );
});
