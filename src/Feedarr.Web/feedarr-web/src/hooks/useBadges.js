import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { apiGet, resolveApiUrl } from "../api/client.js";

const ACTIVITY_LAST_SEEN_KEY = "feedarr:lastSeen:activity";
const RELEASES_LAST_SEEN_KEY = "feedarr:lastSeen:releases";
const RELEASES_LAST_SEEN_TS_KEY = "feedarr:lastSeen:releases_ts";
const UPDATE_LAST_SEEN_TAG_KEY = "feedarr:lastSeenReleaseTag";
const UPDATE_CACHE_KEY = "feedarr:update:latest";
const UPDATE_LAST_CHECK_TS_KEY = "feedarr:update:lastCheckTs";

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
  const timer = useRef(null);
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

  const refresh = useCallback(async () => {
    if (refreshInFlight.current) return;
    refreshInFlight.current = true;
    try {
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
      const hasExactUnseenCount = Number.isFinite(releasesNewSinceTsCount) && releasesNewSinceTsCount >= 0;
      const exactUnseenCount = hasExactUnseenCount ? Math.trunc(releasesNewSinceTsCount) : null;

      const releasesDelta = typeof releasesCount === "number"
        ? Math.max(0, releasesCount - lastSeenReleasesCount)
        : null;
      const hasNewByTs = typeof releasesLatestTs === "number"
        ? releasesLatestTs > lastSeenReleasesTs
        : false;
      const releasesBadgeValue = hasExactUnseenCount
        ? (exactUnseenCount > 0 ? exactUnseenCount : 0)
        : releasesDelta && releasesDelta > 0
          ? releasesDelta
          : hasNewByTs
            ? "warn"
            : 0;

      // Extraction des tâches (retro fetch, sync, etc.)
      const tasks = Array.isArray(sys?.tasks)
        ? sys.tasks
        : Array.isArray(sys?.Tasks)
          ? sys.Tasks
          : [];

      const settingsMissing = (() => {
        if (extRes.status !== "fulfilled") return null;
        const ext = extRes.value || {};
        const missing = [
          !ext.hasTmdbApiKey,
          !ext.hasIgdbClientId,
          !ext.hasIgdbClientSecret,
        ].filter(Boolean).length;
        return missing;
      })();

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
        isUpdateAvailable,
        hasUnseenUpdate,
        latestUpdateTag,
        tasks,
      }));
    } catch {
      // backend down => on garde l'etat precedent
    } finally {
      refreshInFlight.current = false;
    }
  }, [activityLimit, activityMode, lastSeenActivityTs, lastSeenReleasesCount, lastSeenReleasesTs]);

  refreshRef.current = refresh;

  const effectivePollMs = useMemo(() => {
    const base = Math.max(60000, Number(pollMs) || 60000);
    if (sseConnected) return Math.max(base, 300000);
    return base;
  }, [pollMs, sseConnected]);

  useEffect(() => {
    refresh();
    timer.current = setInterval(refresh, effectivePollMs);
    return () => timer.current && clearInterval(timer.current);
  }, [refresh, effectivePollMs]);

  useEffect(() => {
    if (typeof window === "undefined" || typeof EventSource === "undefined") return;
    const url = resolveApiUrl("/api/badges/stream");
    const es = new EventSource(url, { withCredentials: true });

    const onReady = () => {
      setSseConnected(true);
      refreshRef.current?.();
    };
    const onBadge = () => refreshRef.current?.();
    const onError = () => {
      setSseConnected(false);
      es.close();
    };
    const onOpen = () => setSseConnected(true);
    es.addEventListener("ready", onReady);
    es.addEventListener("badge", onBadge);
    es.addEventListener("error", onError);
    es.addEventListener("open", onOpen);

    return () => {
      setSseConnected(false);
      es.removeEventListener("ready", onReady);
      es.removeEventListener("badge", onBadge);
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

  return {
    ...badges,
    lastSeenActivityTs,
    markActivitySeen,
    lastSeenReleasesCount,
    markReleasesSeen,
    lastSeenReleasesTs,
  };
}
