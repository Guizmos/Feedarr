import { matchRoute } from "./pathUtils.js";

export const ACTIVITY_LAST_SEEN_KEY = "feedarr:lastSeen:activity";
export const RELEASES_LAST_SEEN_COUNT_KEY = "feedarr:lastSeen:releases";
export const RELEASES_LAST_SEEN_TS_KEY = "feedarr:lastSeen:releases_ts";
export const UPDATE_LAST_SEEN_TAG_KEY = "feedarr:lastSeenReleaseTag";
export const UPDATE_LAST_SEEN_AT_KEY = "feedarr:lastSeenReleaseTag:ts";

function toNumberOrFallback(value, fallback = 0) {
  const parsed = Number(value);
  return Number.isFinite(parsed) ? parsed : fallback;
}

function toCount(value, fallback = 0) {
  const parsed = toNumberOrFallback(value, fallback);
  return Math.max(0, Math.trunc(parsed));
}

export function parseTs(value) {
  if (value == null) return 0;
  if (typeof value === "number") return Number.isFinite(value) ? value : 0;
  const asNumber = Number(value);
  if (Number.isFinite(asNumber)) return asNumber;
  const parsed = Date.parse(value);
  return Number.isFinite(parsed) ? parsed : 0;
}

export function computeReleasesBadgeValue({
  releasesNewSinceTsCount,
  releasesCount,
  releasesLatestTs,
  lastSeenReleasesCount,
  lastSeenReleasesTs,
}) {
  const safeLastSeenTs = Number.isFinite(Number(lastSeenReleasesTs))
    ? Number(lastSeenReleasesTs)
    : 0;
  const safeLatestTs = Number.isFinite(Number(releasesLatestTs))
    ? Number(releasesLatestTs)
    : 0;
  const safeTotalCount = Number.isFinite(Number(releasesCount))
    ? Math.max(0, Math.trunc(Number(releasesCount)))
    : NaN;
  const safeSeenCount = Number.isFinite(Number(lastSeenReleasesCount))
    ? Math.max(0, Math.trunc(Number(lastSeenReleasesCount)))
    : 0;
  const unseenRaw = Number(releasesNewSinceTsCount);
  const hasExactUnseenCount = Number.isFinite(unseenRaw) && unseenRaw >= 0;
  const hasReliableTsPair = safeLastSeenTs > 0 && safeLatestTs > 0;
  const hasNewByTs = safeLatestTs > 0 && safeLatestTs > safeLastSeenTs;

  // Cursor always wins to avoid stale count races after markSeen.
  if (hasReliableTsPair && !hasNewByTs) {
    return 0;
  }

  if (hasExactUnseenCount) {
    const exactUnseenCount = Math.trunc(unseenRaw);
    return exactUnseenCount > 0 ? exactUnseenCount : 0;
  }

  const releasesDelta = Number.isFinite(safeTotalCount)
    ? Math.max(0, safeTotalCount - safeSeenCount)
    : 0;

  if (hasReliableTsPair) {
    if (!hasNewByTs) return 0;
    return releasesDelta > 0 ? releasesDelta : "warn";
  }

  if (releasesDelta > 0) return releasesDelta;
  if (hasNewByTs) return "warn";
  return 0;
}

export function computeActivityBadgeValue({ latestCursor, seenCursor, latestMeta }) {
  const latestTs = toNumberOrFallback(latestCursor, 0);
  const seenTs = toNumberOrFallback(seenCursor, 0);
  if (latestTs <= 0 || latestTs <= seenTs) return 0;
  return toCount(latestMeta?.unreadCount, 0);
}

export function computeUpdatesBadgeValue({ latestCursor, seenCursor, latestMeta }) {
  const latestTag = String(latestCursor || "");
  const seenTag = String(seenCursor || "");
  const isUpdateAvailable = !!latestMeta?.isUpdateAvailable;
  return !!(isUpdateAvailable && latestTag && latestTag !== seenTag);
}

export const badgeRegistry = [
  {
    key: "releases",
    isNotificationBadge: true,
    cursorType: "number",
    storageKeys: {
      seenCursorKey: RELEASES_LAST_SEEN_TS_KEY,
      seenMetaKey: RELEASES_LAST_SEEN_COUNT_KEY,
      seenMetaType: "number",
    },
    routeMatchers: [{ path: "/library", prefix: true }],
    selectLatest(summary) {
      const releases = summary?.releases || {};
      return {
        cursor: parseTs(releases?.latestTs ?? 0),
        meta: {
          totalCount: toCount(releases?.totalCount, 0),
          newSinceTsCount: releases?.newSinceTsCount == null
            ? NaN
            : toCount(releases?.newSinceTsCount, 0),
        },
      };
    },
    computeValue({ latestCursor, latestMeta, seenCursor, seenMeta }) {
      return computeReleasesBadgeValue({
        releasesNewSinceTsCount: latestMeta?.newSinceTsCount,
        releasesCount: latestMeta?.totalCount,
        releasesLatestTs: latestCursor,
        lastSeenReleasesCount: seenMeta?.value,
        lastSeenReleasesTs: seenCursor,
      });
    },
  },
  {
    key: "activity",
    isNotificationBadge: true,
    cursorType: "number",
    storageKeys: {
      seenCursorKey: ACTIVITY_LAST_SEEN_KEY,
    },
    routeMatchers: [{ path: "/activity", prefix: true }],
    selectLatest(summary) {
      const activity = summary?.activity || {};
      const toneRaw = String(activity?.tone || "info").toLowerCase();
      const tone = toneRaw === "error" || toneRaw === "warn" ? toneRaw : "info";
      return {
        cursor: parseTs(activity?.lastActivityTs ?? 0),
        meta: {
          unreadCount: toCount(activity?.unreadCount, 0),
          tone,
        },
      };
    },
    computeValue(args) {
      return computeActivityBadgeValue(args);
    },
    computeTone({ latestMeta }) {
      const toneRaw = String(latestMeta?.tone || "info").toLowerCase();
      return toneRaw === "error" || toneRaw === "warn" ? toneRaw : "info";
    },
  },
  {
    key: "updates",
    isNotificationBadge: true,
    cursorType: "string",
    stringMerge: "seenAt",
    storageKeys: {
      seenCursorKey: UPDATE_LAST_SEEN_TAG_KEY,
      seenMetaKey: UPDATE_LAST_SEEN_AT_KEY,
      seenMetaType: "number",
    },
    routeMatchers: [{ path: "/system/updates", prefix: true }],
    selectLatest(_summary, updatePayload) {
      const latestTag = String(updatePayload?.latestRelease?.tagName || "");
      return {
        cursor: latestTag,
        meta: {
          isUpdateAvailable: !!updatePayload?.isUpdateAvailable,
          latestRelease: updatePayload?.latestRelease || null,
          releases: Array.isArray(updatePayload?.releases) ? updatePayload.releases : [],
          enabled: updatePayload?.enabled !== false,
          checkIntervalHours: toNumberOrFallback(updatePayload?.checkIntervalHours, 6),
          currentVersion: String(updatePayload?.currentVersion || "0.0.0"),
        },
      };
    },
    computeValue(args) {
      return computeUpdatesBadgeValue(args);
    },
  },
];

const badgeRegistryByKey = new Map(badgeRegistry.map((badge) => [badge.key, badge]));

export function getBadgeDefinition(key) {
  return badgeRegistryByKey.get(key) || null;
}

export function doesBadgeMatchPath(definition, normalizedPath) {
  return (definition?.routeMatchers || []).some((matcher) => matchRoute(normalizedPath, matcher));
}

export function selectRouteSeenKeys({ normalizedPath, latestByKey }) {
  const keys = [];
  for (const definition of badgeRegistry) {
    if (!definition.isNotificationBadge) continue;
    if (!doesBadgeMatchPath(definition, normalizedPath)) continue;
    const latestCursor = latestByKey?.[definition.key]?.cursor;
    if (definition.cursorType === "number") {
      if (!Number.isFinite(Number(latestCursor)) || Number(latestCursor) <= 0) continue;
    } else {
      if (!String(latestCursor || "")) continue;
    }
    keys.push(definition.key);
  }
  return keys;
}
