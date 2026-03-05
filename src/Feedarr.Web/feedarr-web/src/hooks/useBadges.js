import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { apiGet, resolveApiUrl } from "../api/client.js";
import usePolling from "./usePolling.js";

const ACTIVITY_LAST_SEEN_KEY = "feedarr:lastSeen:activity";
const RELEASES_LAST_SEEN_KEY = "feedarr:lastSeen:releases";
const RELEASES_LAST_SEEN_TS_KEY = "feedarr:lastSeen:releases_ts";
const UPDATE_LAST_SEEN_TAG_KEY = "feedarr:lastSeenReleaseTag";
const UPDATE_CACHE_KEY = "feedarr:update:latest";
const UPDATE_LAST_CHECK_TS_KEY = "feedarr:update:lastCheckTs";
const IS_DEV =
  typeof import.meta !== "undefined"
  && typeof import.meta.env !== "undefined"
  && !!import.meta.env.DEV;

function debugBadges(message, details) {
  if (!IS_DEV || typeof console === "undefined") return;
  if (details === undefined) {
    console.debug(`[badges] ${message}`);
    return;
  }
  console.debug(`[badges] ${message}`, details);
}

function isLibraryBadgeVisible(value) {
  return value != null && value !== false && value !== 0;
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

  if (hasExactUnseenCount) {
    const exactUnseenCount = Math.trunc(unseenRaw);
    return exactUnseenCount > 0 ? exactUnseenCount : 0;
  }

  const releasesDelta = Number.isFinite(safeTotalCount)
    ? Math.max(0, safeTotalCount - safeSeenCount)
    : 0;
  const hasNewByTs = safeLatestTs > 0 && safeLatestTs > safeLastSeenTs;
  const hasReliableTsPair = safeLastSeenTs > 0 && safeLatestTs > 0;

  // Once we have a reliable seen/latest timestamp pair, timestamp wins over count delta.
  if (hasReliableTsPair) {
    if (!hasNewByTs) return 0;
    return releasesDelta > 0 ? releasesDelta : "warn";
  }

  if (releasesDelta > 0) return releasesDelta;
  if (hasNewByTs) return "warn";
  return 0;
}

function readCachedUpdate() {
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

function persistCachedUpdate(payload) {
  if (typeof window === "undefined") return;
  window.localStorage.setItem(UPDATE_CACHE_KEY, JSON.stringify(payload || {}));
  window.localStorage.setItem(UPDATE_LAST_CHECK_TS_KEY, String(Date.now()));
}

export async function runSummaryRefreshWithFallback({ state, runSummary, runLegacy }) {
  if (state?.legacyOnly) {
    return runLegacy();
  }

  try {
    return await runSummary();
  } catch (error) {
    const status = Number(error?.status ?? 0);
    if (status === 404 || status === 405 || status === 501) {
      if (state) state.legacyOnly = true;
      return runLegacy();
    }
    throw error;
  }
}

export function createBadgeSseRefreshScheduler(
  refresh,
  {
    minIntervalMs = 1000,
    now = () => Date.now(),
    setTimer = (cb, ms) => setTimeout(cb, ms),
    clearTimer = (id) => clearTimeout(id),
  } = {}
) {
  const intervalMs = Math.max(250, Number(minIntervalMs) || 1000);
  let timerId = null;
  let hasRun = false;
  let lastRunAt = 0;
  let pending = false;
  let disposed = false;

  const invoke = () => {
    if (disposed) return;
    pending = false;
    hasRun = true;
    lastRunAt = now();
    Promise.resolve()
      .then(() => refresh())
      .catch(() => {});
  };

  const schedule = () => {
    if (disposed || !pending || timerId != null) return;

    const elapsed = hasRun ? now() - lastRunAt : Number.POSITIVE_INFINITY;
    const waitMs = elapsed >= intervalMs ? 0 : intervalMs - elapsed;

    if (waitMs <= 0) {
      invoke();
      return;
    }

    timerId = setTimer(() => {
      timerId = null;
      if (!pending || disposed) return;
      invoke();
    }, waitMs);
  };

  return {
    trigger() {
      if (disposed) return;
      pending = true;
      schedule();
    },
    dispose() {
      disposed = true;
      pending = false;
      if (timerId != null) {
        clearTimer(timerId);
        timerId = null;
      }
    },
  };
}

/**
 * Badges Sonarr-like
 * - activity: nb d'events non-info (ou error-only si tu veux)
 * - system: "warn" | "error" | null (affiche "!" avec la couleur)
 */
export default function useBadges({
  pollMs = 60000,
  activityLimit = 200,
  activityMode = "nonInfo", // "nonInfo" | "errorOnly"
} = {}) {
  const [lastSeenActivityTs, setLastSeenActivityTsState] = useState(() =>
    typeof window === "undefined" ? 0 : Number(window.localStorage.getItem(ACTIVITY_LAST_SEEN_KEY) || 0)
  );

  const [lastSeenReleasesCount, setLastSeenReleasesCountState] = useState(() =>
    typeof window === "undefined" ? 0 : Number(window.localStorage.getItem(RELEASES_LAST_SEEN_KEY) || 0)
  );

  const [lastSeenReleasesTs, setLastSeenReleasesTsState] = useState(() =>
    typeof window === "undefined" ? 0 : Number(window.localStorage.getItem(RELEASES_LAST_SEEN_TS_KEY) || 0)
  );

  const [badges, setBadges] = useState({
    activity: 0,
    activityTone: "info",
    system: null,
    sources: 0,
    releases: 0,
    settingsMissing: 0,
    latestActivityTs: 0,
    latestReleasesCount: 0,
    latestReleasesTs: 0,
    isUpdateAvailable: false,
    hasUnseenUpdate: false,
    latestUpdateTag: "",
    tasks: [],
  });
  const [sseConnected, setSseConnected] = useState(false);

  function parseTs(value) {
    if (value == null) return 0;
    if (typeof value === "number") return Number.isFinite(value) ? value : 0;
    const asNumber = Number(value);
    if (Number.isFinite(asNumber)) return asNumber;
    const parsed = Date.parse(value);
    return Number.isFinite(parsed) ? parsed : 0;
  }

  function setLastSeenActivityTs(ts) {
    if (typeof window === "undefined") return;
    const value = Number(ts || 0);
    window.localStorage.setItem(ACTIVITY_LAST_SEEN_KEY, String(value));
  }

  function setLastSeenReleasesCount(count) {
    if (typeof window === "undefined") return;
    const value = Number(count || 0);
    window.localStorage.setItem(RELEASES_LAST_SEEN_KEY, String(value));
  }

  function setLastSeenReleasesTs(ts) {
    if (typeof window === "undefined") return;
    const value = Number(ts || 0);
    window.localStorage.setItem(RELEASES_LAST_SEEN_TS_KEY, String(value));
  }

  const markActivitySeen = useCallback((ts) => {
    const next = Number(ts || 0);
    setLastSeenActivityTs(next);
    setLastSeenActivityTsState(next);
    setBadges((prev) => ({ ...prev, activity: 0, latestActivityTs: next }));
  }, []);

  const markReleasesSeen = useCallback((count, latestTs) => {
    const next = Number(count || 0);
    const nextTs = Number(latestTs || 0);
    setLastSeenReleasesCount(next);
    setLastSeenReleasesCountState(next);
    setBadges((prev) => ({
      ...prev,
      releases: 0,
      latestReleasesCount: next > 0 ? next : prev.latestReleasesCount,
      latestReleasesTs: nextTs > 0 ? nextTs : prev.latestReleasesTs,
    }));
    if (nextTs > 0) {
      setLastSeenReleasesTs(nextTs);
      setLastSeenReleasesTsState(nextTs);
    }
  }, []);

  const refreshInFlight = useRef(false);
  const refreshRef = useRef(null);
  const summaryModeRef = useRef({ legacyOnly: false });
  const libraryBadgeDebugContextRef = useRef(null);
  const previousLibraryBadgeVisibleRef = useRef(false);

  useEffect(() => {
    debugBadges("mount useBadges");
    return () => debugBadges("unmount useBadges");
  }, []);

  const resolveUpdateState = useCallback(async () => {
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

  const refreshLegacy = useCallback(async () => {
    const safeSinceTs = Math.max(0, Number(lastSeenReleasesTs || 0));
    const systemUrl = `/api/system/status?releasesSinceTs=${encodeURIComponent(String(safeSinceTs))}`;

    const [actRes, sysRes, extRes, uiRes] = await Promise.allSettled([
      apiGet(`/api/activity?limit=${activityLimit}`),
      apiGet(systemUrl),
      apiGet("/api/settings/external"),
      apiGet("/api/settings/ui"),
    ]);

    const ui = uiRes.status === "fulfilled" ? uiRes.value : null;
    const badgeLevels = ui
      ? {
          info: !!ui.badgeInfo,
          warn: !!ui.badgeWarn,
          error: !!ui.badgeError,
        }
      : null;

    const activityInfo = (() => {
      if (actRes.status !== "fulfilled") return null;

      // ton API renvoie { value: [...], Count: n }
      const rows = Array.isArray(actRes.value?.value)
        ? actRes.value.value
        : Array.isArray(actRes.value)
          ? actRes.value
          : [];

      const lastSeen = lastSeenActivityTs;

      const withTs = rows
        .map((x) => ({
          item: x,
          ts: parseTs(x?.createdAt ?? x?.createdAtTs ?? x?.created_at_ts ?? x?.timestamp ?? 0),
        }))
        .filter((x) => Number.isFinite(x.ts) && x.ts > 0);

      const latestActivityTs = withTs.reduce(
        (max, x) => (x.ts > max ? x.ts : max),
        0
      );

      const levelOf = (x) => String(x?.level || x?.Level || "").toLowerCase();
      const candidateFilter = (x) => {
        const lvl = levelOf(x);
        if (badgeLevels) {
          if (lvl === "info") return badgeLevels.info;
          if (lvl === "warn" || lvl === "warning") return badgeLevels.warn;
          if (lvl === "error") return badgeLevels.error;
          return false;
        }
        if (activityMode === "errorOnly") return lvl === "error";
        // badge si ce n'est pas info (warn/error/etc)
        return lvl && lvl !== "info";
      };

      const candidates = rows.filter(candidateFilter);

      const activityTone = (() => {
        const levels = candidates.map((x) => levelOf(x));
        if (levels.some((lvl) => lvl === "error")) return "error";
        if (levels.some((lvl) => lvl === "warn" || lvl === "warning")) return "warn";
        return "info";
      })();

      const activityCount = withTs.filter((x) => x.ts > lastSeen && candidateFilter(x.item)).length;

      return { activityCount, latestActivityTs, activityTone };
    })();

    const sys = sysRes.status === "fulfilled" ? sysRes.value : null;

    // System badge: on tente /api/system/status si dispo
    const systemTone = (() => {
      if (!sys) return null;
      const status = String(sys?.status ?? sys?.Status ?? "").toLowerCase();
      if (sys?.ok === false) return "error";
      if (status === "error") return "error";
      if (status === "warn" || status === "warning") return "warn";
      if (Number(sys?.errors ?? sys?.Errors ?? 0) > 0) return "error";
      if (Number(sys?.warnings ?? sys?.Warnings ?? 0) > 0) return "warn";
      return null;
    })();

    const sourcesCount = sys
      ? Number(sys?.sourcesCount ?? sys?.SourcesCount ?? 0)
      : null;
    const releasesCount = sys
      ? Number(sys?.releasesCount ?? sys?.ReleasesCount ?? 0)
      : null;
    const releasesLatestTs = sys
      ? parseTs(sys?.releasesLatestTs ?? sys?.ReleasesLatestTs ?? 0)
      : null;
    const releasesNewSinceTsRaw = sys
      ? (sys?.releasesNewSinceTsCount ?? sys?.ReleasesNewSinceTsCount)
      : null;
    const releasesNewSinceTsCount = releasesNewSinceTsRaw == null
      ? NaN
      : Number(releasesNewSinceTsRaw);
    const releasesBadgeValue = computeReleasesBadgeValue({
      releasesNewSinceTsCount,
      releasesCount,
      releasesLatestTs,
      lastSeenReleasesCount,
      lastSeenReleasesTs,
    });
    libraryBadgeDebugContextRef.current = {
      source: "legacy",
      summary: {
        releasesCount,
        releasesLatestTs,
        releasesNewSinceTsCount,
      },
      sinceTs: safeSinceTs,
      lastSeenTs: lastSeenReleasesTs,
      lastSeenCount: lastSeenReleasesCount,
      documentHidden: typeof document === "undefined" ? null : document.hidden,
      pathname: typeof window === "undefined" ? "" : window.location.pathname,
      computedBadge: releasesBadgeValue,
    };

    // Extraction des tâches (retro fetch, sync, etc.)
    const tasks = Array.isArray(sys?.tasks)
      ? sys.tasks
      : Array.isArray(sys?.Tasks)
        ? sys.Tasks
        : [];

    const settingsMissing = (() => {
      if (extRes.status !== "fulfilled") return null;
      const ext = extRes.value || {};
      return [
        !ext.hasTmdbApiKey,
        !ext.hasIgdbClientId,
        !ext.hasIgdbClientSecret,
      ].filter(Boolean).length;
    })();

    const updateState = await resolveUpdateState();

    setBadges((prev) => ({
      activity: activityInfo?.activityCount ?? prev.activity,
      activityTone: activityInfo?.activityTone ?? prev.activityTone,
      system: systemTone ?? prev.system,
      sources: sourcesCount ?? prev.sources,
      releases: releasesBadgeValue ?? prev.releases,
      settingsMissing: settingsMissing ?? prev.settingsMissing,
      latestActivityTs: activityInfo?.latestActivityTs ?? prev.latestActivityTs,
      latestReleasesCount: releasesCount ?? prev.latestReleasesCount,
      latestReleasesTs: releasesLatestTs ?? prev.latestReleasesTs,
      isUpdateAvailable: updateState.isUpdateAvailable,
      hasUnseenUpdate: updateState.hasUnseenUpdate,
      latestUpdateTag: updateState.latestUpdateTag,
      tasks,
    }));
  }, [activityLimit, activityMode, lastSeenActivityTs, lastSeenReleasesCount, lastSeenReleasesTs, resolveUpdateState]);

  const refreshSummary = useCallback(async () => {
    const safeActivitySinceTs = Math.max(0, Number(lastSeenActivityTs || 0));
    const safeReleasesSinceTs = Math.max(0, Number(lastSeenReleasesTs || 0));
    const safeActivityLimit = Math.max(1, Math.min(500, Number(activityLimit) || 200));
    const query = new URLSearchParams({
      activitySinceTs: String(safeActivitySinceTs),
      releasesSinceTs: String(safeReleasesSinceTs),
      activityLimit: String(safeActivityLimit),
    });

    const summary = await apiGet(`/api/badges/summary?${query.toString()}`);
    const activity = summary?.activity || {};
    const releases = summary?.releases || {};
    const system = summary?.system || {};
    const settings = summary?.settings || {};

    const activityCount = Number(activity?.unreadCount ?? NaN);
    const latestActivityTs = parseTs(activity?.lastActivityTs ?? 0);
    const activityToneRaw = String(activity?.tone || "info").toLowerCase();
    const activityTone = activityToneRaw === "error" || activityToneRaw === "warn" ? activityToneRaw : "info";

    const sourcesCount = Number(system?.sourcesCount ?? NaN);
    const releasesCount = Number(releases?.totalCount ?? NaN);
    const releasesLatestTs = parseTs(releases?.latestTs ?? 0);
    const releasesNewSinceTsRaw = releases?.newSinceTsCount;
    const releasesNewSinceTsCount = releasesNewSinceTsRaw == null ? NaN : Number(releasesNewSinceTsRaw);
    const releasesBadgeValue = computeReleasesBadgeValue({
      releasesNewSinceTsCount,
      releasesCount,
      releasesLatestTs,
      lastSeenReleasesCount,
      lastSeenReleasesTs,
    });
    libraryBadgeDebugContextRef.current = {
      source: "summary",
      summary: {
        activityUnreadCount: activity?.unreadCount ?? null,
        activityLastActivityTs: activity?.lastActivityTs ?? null,
        releasesTotalCount: releases?.totalCount ?? null,
        releasesLatestTs: releases?.latestTs ?? null,
        releasesNewSinceTsCount: releases?.newSinceTsCount ?? null,
      },
      sinceTs: safeReleasesSinceTs,
      lastSeenTs: lastSeenReleasesTs,
      lastSeenCount: lastSeenReleasesCount,
      documentHidden: typeof document === "undefined" ? null : document.hidden,
      pathname: typeof window === "undefined" ? "" : window.location.pathname,
      computedBadge: releasesBadgeValue,
    };

    const settingsMissing = Number(settings?.missingExternalCount ?? NaN);
    const updateState = await resolveUpdateState();

    setBadges((prev) => ({
      activity: Number.isFinite(activityCount) ? Math.max(0, Math.trunc(activityCount)) : prev.activity,
      activityTone,
      system: prev.system,
      sources: Number.isFinite(sourcesCount) ? Math.max(0, Math.trunc(sourcesCount)) : prev.sources,
      releases: releasesBadgeValue ?? prev.releases,
      settingsMissing: Number.isFinite(settingsMissing) ? Math.max(0, Math.trunc(settingsMissing)) : prev.settingsMissing,
      latestActivityTs: latestActivityTs > 0 ? latestActivityTs : prev.latestActivityTs,
      latestReleasesCount: Number.isFinite(releasesCount) ? Math.max(0, Math.trunc(releasesCount)) : prev.latestReleasesCount,
      latestReleasesTs: releasesLatestTs > 0 ? releasesLatestTs : prev.latestReleasesTs,
      isUpdateAvailable: updateState.isUpdateAvailable,
      hasUnseenUpdate: updateState.hasUnseenUpdate,
      latestUpdateTag: updateState.latestUpdateTag,
      tasks: Array.isArray(system?.tasks) ? system.tasks : prev.tasks,
    }));
  }, [activityLimit, lastSeenActivityTs, lastSeenReleasesCount, lastSeenReleasesTs, resolveUpdateState]);

  const refresh = useCallback(async () => {
    if (refreshInFlight.current) return;
    refreshInFlight.current = true;
    try {
      await runSummaryRefreshWithFallback({
        state: summaryModeRef.current,
        runSummary: refreshSummary,
        runLegacy: refreshLegacy,
      });
    } catch {
      // backend down => on garde l'etat precedent
    } finally {
      refreshInFlight.current = false;
    }
  }, [refreshLegacy, refreshSummary]);

  refreshRef.current = refresh;

  const effectivePollMs = useMemo(() => {
    const base = Math.max(60000, Number(pollMs) || 60000);
    if (sseConnected) return Math.max(base, 300000);
    return base;
  }, [pollMs, sseConnected]);

  usePolling(refresh, effectivePollMs);

  useEffect(() => {
    if (typeof window === "undefined" || typeof EventSource === "undefined") return undefined;

    const url = resolveApiUrl("/api/badges/stream");
    const es = new EventSource(url, { withCredentials: true });
    const scheduler = createBadgeSseRefreshScheduler(
      () => refreshRef.current?.(),
      { minIntervalMs: 1000 }
    );

    const onReady = () => {
      setSseConnected(true);
      refreshRef.current?.();
    };
    const onBadgeChanged = () => scheduler.trigger();
    const onError = () => {
      setSseConnected(false);
      es.close();
    };
    const onOpen = () => setSseConnected(true);
    es.addEventListener("ready", onReady);
    es.addEventListener("badge", onBadgeChanged);
    es.addEventListener("badges-changed", onBadgeChanged);
    es.addEventListener("error", onError);
    es.addEventListener("open", onOpen);

    return () => {
      setSseConnected(false);
      scheduler.dispose();
      es.removeEventListener("ready", onReady);
      es.removeEventListener("badge", onBadgeChanged);
      es.removeEventListener("badges-changed", onBadgeChanged);
      es.removeEventListener("error", onError);
      es.removeEventListener("open", onOpen);
      es.close();
    };
  }, []); // stable: EventSource connection created once, never recreated

  useEffect(() => {
    if (typeof window === "undefined") return undefined;

    const onUpdateSignal = () => refreshRef.current?.();
    window.addEventListener("feedarr:update-ack", onUpdateSignal);
    window.addEventListener("feedarr:update-refreshed", onUpdateSignal);
    return () => {
      window.removeEventListener("feedarr:update-ack", onUpdateSignal);
      window.removeEventListener("feedarr:update-refreshed", onUpdateSignal);
    };
  }, []);

  useEffect(() => {
    const isVisible = isLibraryBadgeVisible(badges.releases);
    const wasVisible = previousLibraryBadgeVisibleRef.current;
    previousLibraryBadgeVisibleRef.current = isVisible;

    if (!wasVisible && isVisible) {
      debugBadges("libraryBadge false->true", {
        releasesBadge: badges.releases,
        ...libraryBadgeDebugContextRef.current,
        documentHidden: typeof document === "undefined" ? null : document.hidden,
        pathname: typeof window === "undefined" ? "" : window.location.pathname,
      });
    }
  }, [badges.releases]);

  return {
    ...badges,
    lastSeenActivityTs,
    markActivitySeen,
    lastSeenReleasesCount,
    markReleasesSeen,
    lastSeenReleasesTs,
  };
}
