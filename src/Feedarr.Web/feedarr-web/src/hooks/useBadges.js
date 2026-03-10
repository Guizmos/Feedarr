import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { apiGet } from "../api/client.js";
import usePolling from "./usePolling.js";
import useUpdateBadge from "./useUpdateBadge.js";
import useBadgeSse from "./useBadgeSse.js";
import useSeenBadges from "./useSeenBadges.js";
import {
  parseTs,
  computeReleasesBadgeValue,
  normalizeActivityTone,
  normalizeSystemTone,
  normalizeReleasesTone,
} from "../badges/badgeMappers.js";

export { createBadgeSseConnector } from "./useBadgeSse.js";

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
  const [badges, setBadges] = useState({
    activity: 0,
    activityTone: "info",
    system: null,
    sources: 0,
    releases: 0,       // always number — never "warn"
    releasesTone: "info", // "info" | "warn"
    settingsMissing: 0,
    latestActivityTs: 0,
    latestReleasesCount: 0,
    latestReleasesTs: 0,
    isUpdateAvailable: false,
    hasUnseenUpdate: false,
    latestUpdateTag: "",
    tasks: [],
  });

  const {
    lastSeenActivityTs,
    lastSeenReleasesCount,
    lastSeenReleasesTs,
    markActivitySeen,
    markReleasesSeen,
  } = useSeenBadges(setBadges);

  const refreshInFlight = useRef(false);
  const refreshRef = useRef(null);
  const summaryModeRef = useRef({ legacyOnly: false });

  const { resolve: resolveUpdateState } = useUpdateBadge();

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
    const hasExactUnseenCount = Number.isFinite(releasesNewSinceTsCount) && releasesNewSinceTsCount >= 0;
    const exactUnseenCount = hasExactUnseenCount ? Math.trunc(releasesNewSinceTsCount) : null;

    const releasesDelta = typeof releasesCount === "number"
      ? Math.max(0, releasesCount - lastSeenReleasesCount)
      : null;
    const hasNewByTs = typeof releasesLatestTs === "number"
      ? releasesLatestTs > lastSeenReleasesTs
      : false;

    const releasesBadgeValue = computeReleasesBadgeValue({ hasExactUnseenCount, exactUnseenCount, releasesDelta });
    const releasesTone = normalizeReleasesTone({ backendToneRaw: "", hasExactUnseenCount, releasesDelta, hasNewByTs });

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
      releasesTone,
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
    const activityTone = normalizeActivityTone(activity?.tone);

    const sourcesCount = Number(system?.sourcesCount ?? NaN);
    const releasesCount = Number(releases?.totalCount ?? NaN);
    const releasesLatestTs = parseTs(releases?.latestTs ?? 0);
    const releasesNewSinceTsRaw = releases?.newSinceTsCount;
    const releasesNewSinceTsCount = releasesNewSinceTsRaw == null ? NaN : Number(releasesNewSinceTsRaw);
    const hasExactUnseenCount = Number.isFinite(releasesNewSinceTsCount) && releasesNewSinceTsCount >= 0;
    const exactUnseenCount = hasExactUnseenCount ? Math.trunc(releasesNewSinceTsCount) : null;

    const releasesDelta = Number.isFinite(releasesCount)
      ? Math.max(0, Math.trunc(releasesCount) - lastSeenReleasesCount)
      : null;
    const hasNewByTs = releasesLatestTs > lastSeenReleasesTs;

    const releasesBadgeValue = computeReleasesBadgeValue({ hasExactUnseenCount, exactUnseenCount, releasesDelta });
    const releasesTone = normalizeReleasesTone({ backendToneRaw: releases?.tone, hasExactUnseenCount, releasesDelta, hasNewByTs });

    // system.tone: consumed from backend summary path.
    // undefined means the field is absent (old server) → keep prev.system.
    // null means no actionable condition → clear the badge.
    const nextSystemTone = normalizeSystemTone(system?.tone);

    const settingsMissing = Number(settings?.missingExternalCount ?? NaN);
    const updateState = await resolveUpdateState();

    setBadges((prev) => ({
      activity: Number.isFinite(activityCount) ? Math.max(0, Math.trunc(activityCount)) : prev.activity,
      activityTone,
      system: nextSystemTone !== undefined ? nextSystemTone : prev.system,
      sources: Number.isFinite(sourcesCount) ? Math.max(0, Math.trunc(sourcesCount)) : prev.sources,
      releases: releasesBadgeValue,
      releasesTone,
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

  const { sseConnected } = useBadgeSse(refreshRef);

  const effectivePollMs = useMemo(() => {
    const base = Math.max(60000, Number(pollMs) || 60000);
    if (sseConnected) return Math.max(base, 300000);
    return base;
  }, [pollMs, sseConnected]);

  usePolling(refresh, effectivePollMs);

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
