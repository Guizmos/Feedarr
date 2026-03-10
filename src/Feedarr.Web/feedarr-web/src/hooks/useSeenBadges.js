import { useCallback, useState } from "react";
import {
  ACTIVITY_LAST_SEEN_KEY,
  RELEASES_LAST_SEEN_KEY,
  RELEASES_LAST_SEEN_TS_KEY,
  setLastSeenActivityTs,
  setLastSeenReleasesCount,
  setLastSeenReleasesTs,
} from "../badges/badgeStorage.js";

/**
 * Manages the "last seen" state for the two dismissable badges:
 * activity/logs and releases/library.
 *
 * Responsibilities:
 * - Initializes the three seen-state values from localStorage on mount.
 * - Exposes markActivitySeen / markReleasesSeen that persist to localStorage,
 *   update the React seen-state, and reset the badge display via setBadges.
 *
 * @param {React.Dispatch} setBadges  Stable setter from useBadges (useState
 *   setter — guaranteed stable by React). Used to clear badge display values
 *   (activity → 0, releases → 0) when a page is visited.
 *
 * @returns {{
 *   lastSeenActivityTs: number,
 *   lastSeenReleasesCount: number,
 *   lastSeenReleasesTs: number,
 *   markActivitySeen: (ts: number) => void,
 *   markReleasesSeen: (count: number, latestTs: number) => void,
 * }}
 */
export default function useSeenBadges(setBadges) {
  const [lastSeenActivityTs, setLastSeenActivityTsState] = useState(() =>
    typeof window === "undefined" ? 0 : Number(window.localStorage.getItem(ACTIVITY_LAST_SEEN_KEY) || 0)
  );

  const [lastSeenReleasesCount, setLastSeenReleasesCountState] = useState(() =>
    typeof window === "undefined" ? 0 : Number(window.localStorage.getItem(RELEASES_LAST_SEEN_KEY) || 0)
  );

  const [lastSeenReleasesTs, setLastSeenReleasesTsState] = useState(() =>
    typeof window === "undefined" ? 0 : Number(window.localStorage.getItem(RELEASES_LAST_SEEN_TS_KEY) || 0)
  );

  // setBadges is a React useState setter — stable across renders, safe as dep.
  const markActivitySeen = useCallback((ts) => {
    const next = Number(ts || 0);
    setLastSeenActivityTs(next);
    setLastSeenActivityTsState(next);
    setBadges((prev) => ({ ...prev, activity: 0, latestActivityTs: next }));
  }, [setBadges]);

  const markReleasesSeen = useCallback((count, latestTs) => {
    const next = Number(count || 0);
    const nextTs = Number(latestTs || 0);
    setLastSeenReleasesCount(next);
    setLastSeenReleasesCountState(next);
    setBadges((prev) => ({
      ...prev,
      releases: 0,
      releasesTone: "info",
      latestReleasesCount: next > 0 ? next : prev.latestReleasesCount,
      latestReleasesTs: nextTs > 0 ? nextTs : prev.latestReleasesTs,
    }));
    if (nextTs > 0) {
      setLastSeenReleasesTs(nextTs);
      setLastSeenReleasesTsState(nextTs);
    }
  }, [setBadges]);

  return {
    lastSeenActivityTs,
    lastSeenReleasesCount,
    lastSeenReleasesTs,
    markActivitySeen,
    markReleasesSeen,
  };
}
