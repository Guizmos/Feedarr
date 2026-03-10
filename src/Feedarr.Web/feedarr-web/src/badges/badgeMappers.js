// ---------------------------------------------------------------------------
// Pure mapping / calculation helpers for badge data.
// No side effects, no DOM access — all functions are safe to unit-test with
// plain Node.js (no React, no browser globals required).
// ---------------------------------------------------------------------------

/**
 * Parses a timestamp value coming from the backend.
 * Accepts Unix seconds (number), ISO strings, or anything coercible to a number.
 * Returns 0 for invalid / missing values.
 *
 * @param {unknown} value
 * @returns {number}
 */
export function parseTs(value) {
  if (value == null) return 0;
  if (typeof value === "number") return Number.isFinite(value) ? value : 0;
  const asNumber = Number(value);
  if (Number.isFinite(asNumber)) return asNumber;
  const parsed = Date.parse(value);
  return Number.isFinite(parsed) ? parsed : 0;
}

/**
 * Computes the numeric releases badge count.
 * Used by both the summary and legacy refresh paths.
 *
 * @param {{ hasExactUnseenCount: boolean, exactUnseenCount: number|null, releasesDelta: number|null }} param
 * @returns {number}
 */
export function computeReleasesBadgeValue({ hasExactUnseenCount, exactUnseenCount, releasesDelta }) {
  if (hasExactUnseenCount) return exactUnseenCount > 0 ? exactUnseenCount : 0;
  if (releasesDelta && releasesDelta > 0) return releasesDelta;
  return 0;
}

/**
 * Normalizes an activity tone string received from the backend.
 *
 * @param {unknown} raw
 * @returns {"info"|"warn"|"error"}
 */
export function normalizeActivityTone(raw) {
  const lower = String(raw || "info").toLowerCase();
  return lower === "error" || lower === "warn" ? lower : "info";
}

/**
 * Normalizes the system tone field from the backend summary response.
 *
 * Returns `undefined` when the field is absent on the payload (old server that
 * did not emit `system.tone`).  The caller should treat `undefined` as "keep
 * previous value" rather than clearing the badge.
 *
 * @param {unknown} raw  The raw `system.tone` value from the API response.
 * @returns {"warn"|"error"|null|undefined}
 */
export function normalizeSystemTone(raw) {
  if (raw === "error") return "error";
  if (raw === "warn") return "warn";
  if (raw === null) return null;
  return undefined; // field absent → keep previous value in the caller
}

/**
 * Computes the releases tone, preferring the backend-supplied value when valid
 * and falling back to local heuristics for backward compatibility.
 *
 * @param {{ backendToneRaw: string, hasExactUnseenCount: boolean, releasesDelta: number|null, hasNewByTs: boolean }} param
 * @returns {"info"|"warn"}
 */
export function normalizeReleasesTone({ backendToneRaw, hasExactUnseenCount, releasesDelta, hasNewByTs }) {
  const lower = String(backendToneRaw || "").toLowerCase();
  if (lower === "warn") return "warn";
  if (lower === "info") return "info";
  // Local fallback: no exact count, no delta, but timestamp shows something new.
  return (!hasExactUnseenCount && !(releasesDelta && releasesDelta > 0) && hasNewByTs)
    ? "warn"
    : "info";
}
