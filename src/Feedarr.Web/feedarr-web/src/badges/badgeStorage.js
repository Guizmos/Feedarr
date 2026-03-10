// ---------------------------------------------------------------------------
// localStorage keys
// ---------------------------------------------------------------------------
export const ACTIVITY_LAST_SEEN_KEY     = "feedarr:lastSeen:activity";
export const RELEASES_LAST_SEEN_KEY     = "feedarr:lastSeen:releases";
export const RELEASES_LAST_SEEN_TS_KEY  = "feedarr:lastSeen:releases_ts";
export const UPDATE_LAST_SEEN_TAG_KEY   = "feedarr:lastSeenReleaseTag";
export const UPDATE_CACHE_KEY           = "feedarr:update:latest";
export const UPDATE_LAST_CHECK_TS_KEY   = "feedarr:update:lastCheckTs";

// ---------------------------------------------------------------------------
// Update-cache helpers
// ---------------------------------------------------------------------------

export function readCachedUpdate() {
  if (typeof window === "undefined") return null;
  const raw = window.localStorage.getItem(UPDATE_CACHE_KEY);
  if (!raw) return null;
  try {
    const parsed = JSON.parse(raw);
    if (!parsed || typeof parsed !== "object") return null;

    const hasLegacyShape = !!parsed.latestRelease && !Object.prototype.hasOwnProperty.call(parsed, "releases");
    const hasInvalidReleases = Object.prototype.hasOwnProperty.call(parsed, "releases") && !Array.isArray(parsed.releases);
    if (hasLegacyShape || hasInvalidReleases) {
      window.localStorage.removeItem(UPDATE_CACHE_KEY);
      window.localStorage.removeItem(UPDATE_LAST_CHECK_TS_KEY);
      return null;
    }

    return parsed;
  } catch {
    return null;
  }
}

export function persistCachedUpdate(payload) {
  if (typeof window === "undefined") return;
  window.localStorage.setItem(UPDATE_CACHE_KEY, JSON.stringify(payload || {}));
  window.localStorage.setItem(UPDATE_LAST_CHECK_TS_KEY, String(Date.now()));
}

// ---------------------------------------------------------------------------
// "Last seen" persistence helpers
// ---------------------------------------------------------------------------

export function setLastSeenActivityTs(ts) {
  if (typeof window === "undefined") return;
  window.localStorage.setItem(ACTIVITY_LAST_SEEN_KEY, String(Number(ts || 0)));
}

export function setLastSeenReleasesCount(count) {
  if (typeof window === "undefined") return;
  window.localStorage.setItem(RELEASES_LAST_SEEN_KEY, String(Number(count || 0)));
}

export function setLastSeenReleasesTs(ts) {
  if (typeof window === "undefined") return;
  window.localStorage.setItem(RELEASES_LAST_SEEN_TS_KEY, String(Number(ts || 0)));
}
