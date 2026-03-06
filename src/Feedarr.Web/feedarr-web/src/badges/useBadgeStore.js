import React, {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useMemo,
  useReducer,
  useRef,
} from "react";
import { apiGet, resolveApiUrl } from "../api/client.js";
import usePolling from "../hooks/usePolling.js";
import {
  badgeRegistry,
  computeReleasesBadgeValue,
  getBadgeDefinition,
  parseTs,
  selectRouteSeenKeys,
} from "./badgeRegistry.js";
import { normalizePath } from "./pathUtils.js";

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

function toNumberOrFallback(value, fallback = 0) {
  const parsed = Number(value);
  return Number.isFinite(parsed) ? parsed : fallback;
}

function toCount(value, fallback = 0) {
  const parsed = toNumberOrFallback(value, fallback);
  return Math.max(0, Math.trunc(parsed));
}

function hasWindowStorage() {
  return typeof window !== "undefined" && typeof window.localStorage !== "undefined";
}

function normalizeUpdatePayload(payload) {
  return {
    isUpdateAvailable: !!payload?.isUpdateAvailable,
    latestRelease: payload?.latestRelease || null,
    releases: Array.isArray(payload?.releases) ? payload.releases : [],
    enabled: payload?.enabled !== false,
    checkIntervalHours: toNumberOrFallback(payload?.checkIntervalHours, 6),
    currentVersion: String(payload?.currentVersion || "0.0.0"),
  };
}

function getUpdateIntervalMs(payload) {
  const normalized = normalizeUpdatePayload(payload);
  return Math.max(1, normalized.checkIntervalHours) * 60 * 60 * 1000;
}

function readCachedUpdate() {
  if (!hasWindowStorage()) return null;
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
  if (!hasWindowStorage()) return;
  window.localStorage.setItem(UPDATE_CACHE_KEY, JSON.stringify(payload || {}));
  window.localStorage.setItem(UPDATE_LAST_CHECK_TS_KEY, String(Date.now()));
}

function createDefaultSeenEntry(definition) {
  return {
    cursor: definition?.cursorType === "string" ? "" : 0,
    meta: {},
  };
}

function parseStoredCursor(definition, value) {
  if (definition?.cursorType === "string") {
    return String(value || "");
  }
  return toNumberOrFallback(value, 0);
}

function parseStoredMeta(definition, value) {
  if (!definition?.storageKeys?.seenMetaKey) return {};
  if (definition.storageKeys.seenMetaType === "number") {
    const parsed = toNumberOrFallback(value, 0);
    return Number.isFinite(parsed) ? { value: parsed } : {};
  }
  const asString = String(value || "");
  return asString ? { value: asString } : {};
}

function readSeenEntryFromStorage(definition, storage) {
  const empty = createDefaultSeenEntry(definition);
  if (!storage || !definition?.storageKeys?.seenCursorKey) return empty;

  const rawCursor = storage.getItem(definition.storageKeys.seenCursorKey);
  const cursor = parseStoredCursor(definition, rawCursor);

  let meta = {};
  if (definition.storageKeys.seenMetaKey) {
    const rawMeta = storage.getItem(definition.storageKeys.seenMetaKey);
    meta = parseStoredMeta(definition, rawMeta);
  }

  return { cursor, meta };
}

export function hydrateSeenState(registry = badgeRegistry, storage = hasWindowStorage() ? window.localStorage : null) {
  const hydrated = {};
  for (const definition of registry) {
    hydrated[definition.key] = readSeenEntryFromStorage(definition, storage);
  }
  return hydrated;
}

export function persistSeenEntry(definition, entry, storage = hasWindowStorage() ? window.localStorage : null) {
  if (!storage || !definition?.storageKeys?.seenCursorKey) return;

  const cursor = entry?.cursor;
  if (definition.cursorType === "string") {
    storage.setItem(definition.storageKeys.seenCursorKey, String(cursor || ""));
  } else {
    storage.setItem(definition.storageKeys.seenCursorKey, String(toNumberOrFallback(cursor, 0)));
  }

  if (definition.storageKeys.seenMetaKey) {
    const value = entry?.meta?.value;
    if (definition.storageKeys.seenMetaType === "number") {
      const numericValue = Number(value);
      if (Number.isFinite(numericValue)) {
        storage.setItem(definition.storageKeys.seenMetaKey, String(numericValue));
      } else {
        storage.removeItem(definition.storageKeys.seenMetaKey);
      }
    } else if (typeof value === "string" && value) {
      storage.setItem(definition.storageKeys.seenMetaKey, value);
    } else {
      storage.removeItem(definition.storageKeys.seenMetaKey);
    }
  }
}

function isSameEntry(a, b) {
  const aValue = a?.meta?.value;
  const bValue = b?.meta?.value;
  return a?.cursor === b?.cursor && aValue === bValue;
}

function mergeMetaValue(definition, currentMetaValue, incomingMetaValue) {
  if (!definition?.storageKeys?.seenMetaKey) return undefined;
  if (definition.storageKeys.seenMetaType === "number") {
    return Math.max(
      toNumberOrFallback(currentMetaValue, 0),
      toNumberOrFallback(incomingMetaValue, 0)
    );
  }
  const incoming = String(incomingMetaValue || "");
  if (incoming) return incoming;
  return String(currentMetaValue || "");
}

function mergeStringCursorBySeenAt(definition, current, incoming) {
  const currentSeenAt = toNumberOrFallback(current?.meta?.value, 0);
  const incomingSeenAt = toNumberOrFallback(incoming?.meta?.value, 0);

  if (incomingSeenAt > currentSeenAt) return String(incoming?.cursor || "");
  if (incomingSeenAt < currentSeenAt) return String(current?.cursor || "");

  const currentCursor = String(current?.cursor || "");
  const incomingCursor = String(incoming?.cursor || "");
  if (!currentCursor && incomingCursor) return incomingCursor;
  if (currentCursor === incomingCursor) return currentCursor;

  if (definition?.stringMerge === "seenAt") {
    // Equal timestamps: keep current to avoid stale regressions.
    return currentCursor;
  }
  return incomingCursor || currentCursor;
}

export function mergeSeenEntry(definition, currentEntry, incomingEntry) {
  const current = currentEntry || createDefaultSeenEntry(definition);
  const incoming = incomingEntry || createDefaultSeenEntry(definition);

  let mergedCursor;
  if (definition?.cursorType === "string") {
    mergedCursor = mergeStringCursorBySeenAt(definition, current, incoming);
  } else {
    mergedCursor = Math.max(
      toNumberOrFallback(current?.cursor, 0),
      toNumberOrFallback(incoming?.cursor, 0)
    );
  }

  const mergedMetaValue = mergeMetaValue(
    definition,
    current?.meta?.value,
    incoming?.meta?.value
  );

  const merged = {
    cursor: mergedCursor,
    meta: mergedMetaValue === undefined ? {} : { value: mergedMetaValue },
  };

  return isSameEntry(current, merged) ? current : merged;
}

export function mergeSeenState(registry, currentSeen, incomingSeen) {
  if (!incomingSeen || typeof incomingSeen !== "object") return currentSeen;
  let changed = false;
  let nextSeen = currentSeen;

  for (const definition of registry) {
    const incomingEntry = incomingSeen[definition.key];
    if (!incomingEntry) continue;
    const currentEntry = currentSeen[definition.key] || createDefaultSeenEntry(definition);
    const mergedEntry = mergeSeenEntry(definition, currentEntry, incomingEntry);
    if (mergedEntry === currentEntry) continue;

    if (!changed) {
      changed = true;
      nextSeen = { ...currentSeen };
    }
    nextSeen[definition.key] = mergedEntry;
  }

  return changed ? nextSeen : currentSeen;
}

function hasCursorValue(definition, cursor) {
  if (definition?.cursorType === "string") {
    return !!String(cursor || "");
  }
  return toNumberOrFallback(cursor, 0) > 0;
}

export function computeBadgeSnapshot(registry, latestByKey, seenByKey) {
  const snapshot = {};
  for (const definition of registry) {
    const latest = latestByKey?.[definition.key] || { cursor: definition.cursorType === "string" ? "" : 0, meta: {} };
    const seen = seenByKey?.[definition.key] || createDefaultSeenEntry(definition);
    const value = definition.computeValue({
      latestCursor: latest.cursor,
      latestMeta: latest.meta || {},
      seenCursor: seen.cursor,
      seenMeta: seen.meta || {},
    });
    const tone = typeof definition.computeTone === "function"
      ? definition.computeTone({
          latestCursor: latest.cursor,
          latestMeta: latest.meta || {},
          seenCursor: seen.cursor,
          seenMeta: seen.meta || {},
          value,
        })
      : undefined;
    snapshot[definition.key] = { value, tone };
  }
  return snapshot;
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

function createInitialLatestState() {
  const byKey = {};
  for (const definition of badgeRegistry) {
    byKey[definition.key] = {
      cursor: definition.cursorType === "string" ? "" : 0,
      meta: {},
    };
  }

  const cachedUpdate = readCachedUpdate();
  const updatesDefinition = getBadgeDefinition("updates");
  if (updatesDefinition) {
    byKey.updates = updatesDefinition.selectLatest(null, cachedUpdate);
  }

  return byKey;
}

function createInitialState() {
  return {
    latest: createInitialLatestState(),
    seen: hydrateSeenState(),
    sseConnected: false,
    updatesStatus: {
      loading: !readCachedUpdate(),
      checking: false,
      error: "",
    },
    extras: {
      systemTone: null,
      sources: 0,
      settingsMissing: 0,
      tasks: [],
    },
  };
}

function reducer(state, action) {
  switch (action.type) {
    case "SET_SEEN":
      if (state.seen === action.seen) return state;
      return { ...state, seen: action.seen };
    case "MERGE_LATEST":
      return {
        ...state,
        latest: {
          ...state.latest,
          ...action.latestPatch,
        },
      };
    case "SET_SSE_CONNECTED":
      if (state.sseConnected === action.value) return state;
      return { ...state, sseConnected: !!action.value };
    case "SET_UPDATES_STATUS":
      return {
        ...state,
        updatesStatus: {
          ...state.updatesStatus,
          ...action.patch,
        },
      };
    case "MERGE_EXTRAS":
      return {
        ...state,
        extras: {
          ...state.extras,
          ...action.patch,
        },
      };
    default:
      return state;
  }
}

function extractActivityInfoFromLegacy({
  activityPayload,
  lastSeenActivityTs,
  activityMode,
  badgeLevels,
}) {
  const rows = Array.isArray(activityPayload?.value)
    ? activityPayload.value
    : Array.isArray(activityPayload)
      ? activityPayload
      : [];

  const levelOf = (item) => String(item?.level || item?.Level || "").toLowerCase();
  const isCandidate = (item) => {
    const level = levelOf(item);
    if (badgeLevels) {
      if (level === "info") return badgeLevels.info;
      if (level === "warn" || level === "warning") return badgeLevels.warn;
      if (level === "error") return badgeLevels.error;
      return false;
    }
    if (activityMode === "errorOnly") return level === "error";
    return level && level !== "info";
  };

  const withTs = rows
    .map((item) => ({
      item,
      ts: parseTs(item?.createdAt ?? item?.createdAtTs ?? item?.created_at_ts ?? item?.timestamp ?? 0),
    }))
    .filter((entry) => Number.isFinite(entry.ts) && entry.ts > 0);

  const latestActivityTs = withTs.reduce((max, entry) => (entry.ts > max ? entry.ts : max), 0);
  const activityCount = withTs.filter((entry) => entry.ts > lastSeenActivityTs && isCandidate(entry.item)).length;
  const candidateLevels = rows.filter(isCandidate).map((item) => levelOf(item));
  const activityTone = candidateLevels.some((level) => level === "error")
    ? "error"
    : candidateLevels.some((level) => level === "warn" || level === "warning")
      ? "warn"
      : "info";

  return { latestActivityTs, activityCount, activityTone };
}

function resolveSystemToneFromLegacy(systemPayload) {
  if (!systemPayload) return null;
  const status = String(systemPayload?.status ?? systemPayload?.Status ?? "").toLowerCase();
  if (systemPayload?.ok === false) return "error";
  if (status === "error") return "error";
  if (status === "warn" || status === "warning") return "warn";
  if (Number(systemPayload?.errors ?? systemPayload?.Errors ?? 0) > 0) return "error";
  if (Number(systemPayload?.warnings ?? systemPayload?.Warnings ?? 0) > 0) return "warn";
  return null;
}

function selectSeenStorageKeys() {
  const keyToDefinition = new Map();
  for (const definition of badgeRegistry) {
    const cursorKey = definition?.storageKeys?.seenCursorKey;
    if (cursorKey) keyToDefinition.set(cursorKey, definition);
    const metaKey = definition?.storageKeys?.seenMetaKey;
    if (metaKey) keyToDefinition.set(metaKey, definition);
  }
  return keyToDefinition;
}

export function useBadgeStore({
  pollMs = 60000,
  activityLimit = 200,
  activityMode = "nonInfo",
} = {}) {
  const [state, dispatch] = useReducer(reducer, undefined, createInitialState);

  const seenRef = useRef(state.seen);
  const latestRef = useRef(state.latest);
  const refreshInFlightRef = useRef(false);
  const refreshRef = useRef(null);
  const summaryModeRef = useRef({ legacyOnly: false });
  const broadcastChannelRef = useRef(null);

  seenRef.current = state.seen;
  latestRef.current = state.latest;

  const updateLatest = useCallback((latestPatch) => {
    dispatch({ type: "MERGE_LATEST", latestPatch });
  }, []);

  const applySeenPatch = useCallback((incomingPatch, { persist = false, broadcast = false } = {}) => {
    const currentSeen = seenRef.current;
    const nextSeen = mergeSeenState(badgeRegistry, currentSeen, incomingPatch);
    if (nextSeen === currentSeen) return false;

    dispatch({ type: "SET_SEEN", seen: nextSeen });

    if (persist && hasWindowStorage()) {
      for (const key of Object.keys(incomingPatch || {})) {
        const definition = getBadgeDefinition(key);
        if (!definition) continue;
        persistSeenEntry(definition, nextSeen[key]);
      }
    }

    if (broadcast) {
      const message = {
        type: "seen-merge",
        entries: incomingPatch,
      };
      if (broadcastChannelRef.current) {
        broadcastChannelRef.current.postMessage(message);
      }
    }

    return true;
  }, []);

  const resolveUpdateLatest = useCallback(async ({ force = false, silent = true } = {}) => {
    if (!silent) {
      dispatch({ type: "SET_UPDATES_STATUS", patch: { checking: true, error: "" } });
    }

    let payload = readCachedUpdate();
    try {
      const lastCheckTs = hasWindowStorage()
        ? toNumberOrFallback(window.localStorage.getItem(UPDATE_LAST_CHECK_TS_KEY), 0)
        : 0;
      const intervalMs = getUpdateIntervalMs(payload);
      const shouldCheck = force || !payload || (Date.now() - lastCheckTs) >= intervalMs;

      if (shouldCheck) {
        const suffix = force ? "?force=true" : "";
        const fetched = await apiGet(`/api/updates/latest${suffix}`);
        if (fetched && typeof fetched === "object") {
          payload = fetched;
          persistCachedUpdate(fetched);
          if (typeof window !== "undefined") {
            window.dispatchEvent(new CustomEvent("feedarr:update-refreshed", { detail: { data: fetched } }));
          }
        }
      }
    } catch (error) {
      if (!silent) {
        dispatch({
          type: "SET_UPDATES_STATUS",
          patch: { error: error?.message || "Impossible de verifier les mises a jour" },
        });
      }
    } finally {
      dispatch({
        type: "SET_UPDATES_STATUS",
        patch: {
          loading: false,
          checking: false,
        },
      });
    }

    const definition = getBadgeDefinition("updates");
    const selected = definition
      ? definition.selectLatest(null, payload)
      : { cursor: "", meta: normalizeUpdatePayload(payload) };
    updateLatest({ updates: selected });
    return selected.meta;
  }, [updateLatest]);

  const markSeen = useCallback((badgeKey) => {
    const definition = getBadgeDefinition(badgeKey);
    if (!definition || !definition.isNotificationBadge) return false;

    const latestEntry = latestRef.current[badgeKey];
    if (!latestEntry || !hasCursorValue(definition, latestEntry.cursor)) return false;

    const incomingEntry = {
      cursor: latestEntry.cursor,
      meta: {},
    };

    if (definition.storageKeys?.seenMetaKey) {
      if (badgeKey === "updates") {
        incomingEntry.meta.value = Date.now();
      } else if (badgeKey === "releases") {
        incomingEntry.meta.value = toCount(latestEntry?.meta?.totalCount, 0);
      }
    }

    const changed = applySeenPatch(
      { [badgeKey]: incomingEntry },
      { persist: true, broadcast: true }
    );

    if (changed && badgeKey === "updates" && typeof window !== "undefined") {
      window.dispatchEvent(
        new CustomEvent("feedarr:update-ack", { detail: { tag: String(latestEntry.cursor || "") } })
      );
    }

    return changed;
  }, [applySeenPatch]);

  const markSeenByRoute = useCallback((pathname, basename) => {
    const normalized = normalizePath(pathname, basename);
    const candidateKeys = selectRouteSeenKeys({
      normalizedPath: normalized,
      latestByKey: latestRef.current,
    });
    for (const key of candidateKeys) {
      markSeen(key);
    }
    return candidateKeys;
  }, [markSeen]);

  const refreshSummary = useCallback(async () => {
    const safeActivitySinceTs = Math.max(0, toNumberOrFallback(seenRef.current?.activity?.cursor, 0));
    const safeReleasesSinceTs = Math.max(0, toNumberOrFallback(seenRef.current?.releases?.cursor, 0));
    const safeActivityLimit = Math.max(1, Math.min(500, Number(activityLimit) || 200));

    const query = new URLSearchParams({
      activitySinceTs: String(safeActivitySinceTs),
      releasesSinceTs: String(safeReleasesSinceTs),
      activityLimit: String(safeActivityLimit),
    });

    const summary = await apiGet(`/api/badges/summary?${query.toString()}`);
    const activityDefinition = getBadgeDefinition("activity");
    const releasesDefinition = getBadgeDefinition("releases");
    const updatesDefinition = getBadgeDefinition("updates");

    const latestPatch = {};
    if (activityDefinition) latestPatch.activity = activityDefinition.selectLatest(summary, null);
    if (releasesDefinition) latestPatch.releases = releasesDefinition.selectLatest(summary, null);

    const updateMeta = await resolveUpdateLatest({ force: false, silent: true });
    if (updatesDefinition) latestPatch.updates = updatesDefinition.selectLatest(summary, updateMeta);

    updateLatest(latestPatch);

    dispatch({
      type: "MERGE_EXTRAS",
      patch: {
        sources: toCount(summary?.system?.sourcesCount, state.extras.sources),
        settingsMissing: toCount(summary?.settings?.missingExternalCount, state.extras.settingsMissing),
        tasks: Array.isArray(summary?.system?.tasks) ? summary.system.tasks : state.extras.tasks,
      },
    });
  }, [activityLimit, resolveUpdateLatest, state.extras.settingsMissing, state.extras.sources, state.extras.tasks, updateLatest]);

  const refreshLegacy = useCallback(async () => {
    const safeReleasesSinceTs = Math.max(0, toNumberOrFallback(seenRef.current?.releases?.cursor, 0));
    const systemUrl = `/api/system/status?releasesSinceTs=${encodeURIComponent(String(safeReleasesSinceTs))}`;

    const [activityRes, systemRes, externalRes, uiRes] = await Promise.allSettled([
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

    const activityInfo = activityRes.status === "fulfilled"
      ? extractActivityInfoFromLegacy({
          activityPayload: activityRes.value,
          lastSeenActivityTs: Math.max(0, toNumberOrFallback(seenRef.current?.activity?.cursor, 0)),
          activityMode,
          badgeLevels,
        })
      : { latestActivityTs: 0, activityCount: 0, activityTone: "info" };

    const systemPayload = systemRes.status === "fulfilled" ? systemRes.value : null;
    const releasesCount = toCount(systemPayload?.releasesCount ?? systemPayload?.ReleasesCount, 0);
    const releasesLatestTs = parseTs(systemPayload?.releasesLatestTs ?? systemPayload?.ReleasesLatestTs ?? 0);
    const releasesNewSinceTsRaw = systemPayload
      ? (systemPayload?.releasesNewSinceTsCount ?? systemPayload?.ReleasesNewSinceTsCount)
      : null;
    const releasesNewSinceTsCount = releasesNewSinceTsRaw == null
      ? NaN
      : toCount(releasesNewSinceTsRaw, 0);

    const updateMeta = await resolveUpdateLatest({ force: false, silent: true });
    const updatesDefinition = getBadgeDefinition("updates");

    updateLatest({
      activity: {
        cursor: activityInfo.latestActivityTs,
        meta: {
          unreadCount: activityInfo.activityCount,
          tone: activityInfo.activityTone,
        },
      },
      releases: {
        cursor: releasesLatestTs,
        meta: {
          totalCount: releasesCount,
          newSinceTsCount: releasesNewSinceTsCount,
        },
      },
      ...(updatesDefinition ? { updates: updatesDefinition.selectLatest(null, updateMeta) } : {}),
    });

    const external = externalRes.status === "fulfilled" ? externalRes.value : null;
    const settingsMissing = external
      ? [
          !external.hasTmdbApiKey,
          !external.hasIgdbClientId,
          !external.hasIgdbClientSecret,
        ].filter(Boolean).length
      : state.extras.settingsMissing;

    const systemTone = resolveSystemToneFromLegacy(systemPayload);
    const sourcesCount = toCount(systemPayload?.sourcesCount ?? systemPayload?.SourcesCount, state.extras.sources);
    const tasks = Array.isArray(systemPayload?.tasks)
      ? systemPayload.tasks
      : Array.isArray(systemPayload?.Tasks)
        ? systemPayload.Tasks
        : state.extras.tasks;

    dispatch({
      type: "MERGE_EXTRAS",
      patch: {
        systemTone,
        sources: sourcesCount,
        settingsMissing,
        tasks,
      },
    });
  }, [activityLimit, activityMode, resolveUpdateLatest, state.extras.settingsMissing, state.extras.sources, state.extras.tasks, updateLatest]);

  const refresh = useCallback(async () => {
    if (refreshInFlightRef.current) return;
    refreshInFlightRef.current = true;
    try {
      await runSummaryRefreshWithFallback({
        state: summaryModeRef.current,
        runSummary: refreshSummary,
        runLegacy: refreshLegacy,
      });
    } catch {
      // keep previous state on backend failures
    } finally {
      refreshInFlightRef.current = false;
    }
  }, [refreshLegacy, refreshSummary]);

  const checkForUpdates = useCallback(async ({ force = false, silent = false } = {}) => {
    return resolveUpdateLatest({ force, silent });
  }, [resolveUpdateLatest]);

  refreshRef.current = refresh;

  const effectivePollMs = useMemo(() => {
    const base = Math.max(60000, Number(pollMs) || 60000);
    if (state.sseConnected) return Math.max(base, 300000);
    return base;
  }, [pollMs, state.sseConnected]);

  useEffect(() => {
    debugBadges("mount useBadgeStore");
    return () => debugBadges("unmount useBadgeStore");
  }, []);

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
      dispatch({ type: "SET_SSE_CONNECTED", value: true });
      refreshRef.current?.();
    };
    const onBadgeChanged = () => scheduler.trigger();
    const onError = () => {
      dispatch({ type: "SET_SSE_CONNECTED", value: false });
      es.close();
    };
    const onOpen = () => dispatch({ type: "SET_SSE_CONNECTED", value: true });

    es.addEventListener("ready", onReady);
    es.addEventListener("badge", onBadgeChanged);
    es.addEventListener("badges-changed", onBadgeChanged);
    es.addEventListener("error", onError);
    es.addEventListener("open", onOpen);

    return () => {
      dispatch({ type: "SET_SSE_CONNECTED", value: false });
      scheduler.dispose();
      es.removeEventListener("ready", onReady);
      es.removeEventListener("badge", onBadgeChanged);
      es.removeEventListener("badges-changed", onBadgeChanged);
      es.removeEventListener("error", onError);
      es.removeEventListener("open", onOpen);
      es.close();
    };
  }, []);

  useEffect(() => {
    if (typeof window === "undefined") return undefined;

    const seenStorageKeys = selectSeenStorageKeys();

    const onStorage = (event) => {
      if (!event?.key) return;

      if (event.key === UPDATE_CACHE_KEY) {
        const updatesDefinition = getBadgeDefinition("updates");
        const cached = readCachedUpdate();
        if (updatesDefinition) {
          updateLatest({ updates: updatesDefinition.selectLatest(null, cached) });
        }
        dispatch({ type: "SET_UPDATES_STATUS", patch: { loading: false } });
        return;
      }

      const definition = seenStorageKeys.get(event.key);
      if (!definition) return;
      const incoming = readSeenEntryFromStorage(definition, window.localStorage);
      applySeenPatch({ [definition.key]: incoming });
    };

    window.addEventListener("storage", onStorage);

    if (typeof BroadcastChannel !== "undefined") {
      const channel = new BroadcastChannel("feedarr:badges");
      broadcastChannelRef.current = channel;
      channel.onmessage = (event) => {
        const data = event?.data;
        if (!data || data.type !== "seen-merge" || !data.entries) return;
        applySeenPatch(data.entries);
      };
    }

    return () => {
      window.removeEventListener("storage", onStorage);
      if (broadcastChannelRef.current) {
        broadcastChannelRef.current.close();
        broadcastChannelRef.current = null;
      }
    };
  }, [applySeenPatch, updateLatest]);

  const computed = useMemo(() => {
    return computeBadgeSnapshot(badgeRegistry, state.latest, state.seen);
  }, [state.latest, state.seen]);

  const releasesBadge = computed?.releases?.value ?? 0;
  const activityBadge = computed?.activity?.value ?? 0;
  const activityTone = computed?.activity?.tone || "info";
  const hasUnseenUpdate = !!computed?.updates?.value;
  const updatesMeta = normalizeUpdatePayload(state.latest?.updates?.meta || {});

  return {
    refresh,
    markSeen,
    markSeenByRoute,
    acknowledgeUpdate: () => markSeen("updates"),

    activity: activityBadge,
    activityTone,
    releases: releasesBadge,
    system: state.extras.systemTone,
    sources: state.extras.sources,
    settingsMissing: state.extras.settingsMissing,
    tasks: state.extras.tasks,

    latestActivityTs: toNumberOrFallback(state.latest?.activity?.cursor, 0),
    lastSeenActivityTs: toNumberOrFallback(state.seen?.activity?.cursor, 0),
    latestReleasesTs: toNumberOrFallback(state.latest?.releases?.cursor, 0),
    latestReleasesCount: toCount(state.latest?.releases?.meta?.totalCount, 0),
    lastSeenReleasesTs: toNumberOrFallback(state.seen?.releases?.cursor, 0),

    latestUpdateTag: String(state.latest?.updates?.cursor || ""),
    lastSeenUpdateTag: String(state.seen?.updates?.cursor || ""),
    isUpdateAvailable: !!updatesMeta.isUpdateAvailable,
    hasUnseenUpdate,

    sseConnected: state.sseConnected,

    updates: {
      loading: state.updatesStatus.loading,
      checking: state.updatesStatus.checking,
      error: state.updatesStatus.error,
      updatesEnabled: updatesMeta.enabled !== false,
      currentVersion: updatesMeta.currentVersion,
      isUpdateAvailable: !!updatesMeta.isUpdateAvailable,
      latestRelease: updatesMeta.latestRelease,
      releases: updatesMeta.releases,
      hasUnseenUpdate,
      checkIntervalHours: updatesMeta.checkIntervalHours,
      checkForUpdates,
      acknowledgeLatest: () => markSeen("updates"),
    },
  };
}

const BadgeStoreContext = createContext(null);

export function BadgeStoreProvider({ children, ...options }) {
  const store = useBadgeStore(options);
  return React.createElement(BadgeStoreContext.Provider, { value: store }, children);
}

export function useBadgeStoreContext() {
  const context = useContext(BadgeStoreContext);
  if (!context) {
    throw new Error("useBadgeStoreContext must be used within BadgeStoreProvider");
  }
  return context;
}

export { normalizePath, computeReleasesBadgeValue };
