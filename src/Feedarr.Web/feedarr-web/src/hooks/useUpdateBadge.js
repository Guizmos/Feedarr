import { useCallback } from "react";
import { apiGet } from "../api/client.js";
import {
  UPDATE_CACHE_KEY,
  UPDATE_LAST_CHECK_TS_KEY,
  UPDATE_LAST_SEEN_TAG_KEY,
  readCachedUpdate,
  persistCachedUpdate,
} from "../badges/badgeStorage.js";

/**
 * Encapsulates the "should I check for a new app update?" logic.
 *
 * Respects the `checkIntervalHours` hint returned by the backend and
 * caches the last payload in localStorage to avoid redundant fetches.
 *
 * @returns {{ resolve: () => Promise<{ isUpdateAvailable: boolean, hasUnseenUpdate: boolean, latestUpdateTag: string }> }}
 */
export default function useUpdateBadge() {
  const resolve = useCallback(async () => {
    let updatePayload = readCachedUpdate();
    try {
      const lastUpdateCheckTs = typeof window === "undefined"
        ? 0
        : Number(window.localStorage.getItem(UPDATE_LAST_CHECK_TS_KEY) || 0);
      const updateIntervalHours = Number(updatePayload?.checkIntervalHours ?? 6);
      const updateIntervalMs = Math.max(1, Number.isFinite(updateIntervalHours) ? updateIntervalHours : 6) * 60 * 60 * 1000;
      const shouldCheckUpdates = !updatePayload || (Date.now() - lastUpdateCheckTs) >= updateIntervalMs;
      if (shouldCheckUpdates) {
        const fetched = await apiGet("/api/updates/latest");
        if (fetched && typeof fetched === "object") {
          updatePayload = fetched;
          persistCachedUpdate(fetched);
        }
      }
    } catch {
      // Keep previous cached update payload on failures.
    }

    const latestUpdateTag = String(updatePayload?.latestRelease?.tagName || "");
    const isUpdateAvailable = !!updatePayload?.isUpdateAvailable;
    const lastSeenUpdateTag = typeof window === "undefined"
      ? ""
      : String(window.localStorage.getItem(UPDATE_LAST_SEEN_TAG_KEY) || "");
    const hasUnseenUpdate = !!(
      isUpdateAvailable
      && latestUpdateTag
      && latestUpdateTag !== lastSeenUpdateTag
    );

    return { isUpdateAvailable, hasUnseenUpdate, latestUpdateTag };
  }, []);

  return { resolve };
}
