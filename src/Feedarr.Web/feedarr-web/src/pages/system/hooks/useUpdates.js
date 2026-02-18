import { useCallback, useEffect, useMemo, useState } from "react";
import { apiGet } from "../../../api/client.js";

const UPDATE_ACK_KEY = "feedarr:lastSeenReleaseTag";
const UPDATE_CACHE_KEY = "feedarr:update:latest";
const UPDATE_LAST_CHECK_TS_KEY = "feedarr:update:lastCheckTs";

function readNumber(key, fallback = 0) {
  if (typeof window === "undefined") return fallback;
  const raw = Number(window.localStorage.getItem(key) || fallback);
  return Number.isFinite(raw) ? raw : fallback;
}

function readString(key, fallback = "") {
  if (typeof window === "undefined") return fallback;
  return String(window.localStorage.getItem(key) || fallback);
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

function persistUpdate(payload) {
  if (typeof window === "undefined") return;
  window.localStorage.setItem(UPDATE_CACHE_KEY, JSON.stringify(payload || {}));
  window.localStorage.setItem(UPDATE_LAST_CHECK_TS_KEY, String(Date.now()));
}

function getIntervalMs(data) {
  const hours = Number(data?.checkIntervalHours ?? 6);
  return Math.max(1, Number.isFinite(hours) ? hours : 6) * 60 * 60 * 1000;
}

export default function useUpdates() {
  const [data, setData] = useState(() => readCachedUpdate());
  const [loading, setLoading] = useState(() => !readCachedUpdate());
  const [checking, setChecking] = useState(false);
  const [error, setError] = useState("");
  const [lastSeenReleaseTag, setLastSeenReleaseTag] = useState(() => readString(UPDATE_ACK_KEY, ""));

  const latestTag = String(data?.latestRelease?.tagName || "");
  const hasUnseenUpdate = !!(data?.isUpdateAvailable && latestTag && latestTag !== lastSeenReleaseTag);

  const checkForUpdates = useCallback(async ({ force = false, silent = false } = {}) => {
    if (!silent) setChecking(true);
    setError("");
    try {
      const suffix = force ? "?force=true" : "";
      const res = await apiGet(`/api/updates/latest${suffix}`);
      setData(res || null);
      persistUpdate(res || {});
      if (typeof window !== "undefined") {
        window.dispatchEvent(new CustomEvent("feedarr:update-refreshed", { detail: { data: res || {} } }));
      }
      return res || null;
    } catch (e) {
      if (!silent) {
        setError(e?.message || "Impossible de verifier les mises a jour");
      }
      return null;
    } finally {
      setLoading(false);
      if (!silent) setChecking(false);
    }
  }, []);

  const acknowledgeLatest = useCallback(() => {
    if (!latestTag || typeof window === "undefined") return;
    window.localStorage.setItem(UPDATE_ACK_KEY, latestTag);
    setLastSeenReleaseTag(latestTag);
    window.dispatchEvent(new CustomEvent("feedarr:update-ack", { detail: { tag: latestTag } }));
  }, [latestTag]);

  useEffect(() => {
    const cached = readCachedUpdate();
    const lastCheck = readNumber(UPDATE_LAST_CHECK_TS_KEY, 0);
    const now = Date.now();
    const intervalMs = getIntervalMs(cached);
    const stale = !cached || (now - lastCheck) >= intervalMs;

    if (stale) {
      checkForUpdates({ force: false, silent: true });
    } else {
      setLoading(false);
      setData(cached);
    }
  }, [checkForUpdates]);

  useEffect(() => {
    const timer = setInterval(() => {
      const lastCheck = readNumber(UPDATE_LAST_CHECK_TS_KEY, 0);
      const intervalMs = getIntervalMs(data);
      if ((Date.now() - lastCheck) >= intervalMs) {
        checkForUpdates({ force: false, silent: true });
      }
    }, 60_000);

    return () => clearInterval(timer);
  }, [checkForUpdates, data]);

  useEffect(() => {
    if (typeof window === "undefined") return undefined;

    function onAck(e) {
      const tag = String(e?.detail?.tag || readString(UPDATE_ACK_KEY, ""));
      setLastSeenReleaseTag(tag);
    }

    function onStorage(e) {
      if (e.key === UPDATE_ACK_KEY) {
        setLastSeenReleaseTag(readString(UPDATE_ACK_KEY, ""));
      }
      if (e.key === UPDATE_CACHE_KEY) {
        setData(readCachedUpdate());
      }
    }

    window.addEventListener("feedarr:update-ack", onAck);
    window.addEventListener("storage", onStorage);
    return () => {
      window.removeEventListener("feedarr:update-ack", onAck);
      window.removeEventListener("storage", onStorage);
    };
  }, []);

  const currentVersion = String(data?.currentVersion || "0.0.0");
  const isUpdateAvailable = !!data?.isUpdateAvailable;
  const latestRelease = data?.latestRelease || null;
  const updatesEnabled = data?.enabled !== false;
  const checkIntervalHours = Number(data?.checkIntervalHours ?? 6);

  return useMemo(() => {
    const releases = Array.isArray(data?.releases) ? data.releases : [];

    return {
      loading,
      checking,
      error,
      updatesEnabled,
      currentVersion,
      isUpdateAvailable,
      latestRelease,
      releases,
      hasUnseenUpdate,
      checkIntervalHours,
      checkForUpdates,
      acknowledgeLatest,
    };
  }, [
    data?.releases,
    loading,
    checking,
    error,
    updatesEnabled,
    currentVersion,
    isUpdateAvailable,
    latestRelease,
    hasUnseenUpdate,
    checkIntervalHours,
    checkForUpdates,
    acknowledgeLatest,
  ]);
}
