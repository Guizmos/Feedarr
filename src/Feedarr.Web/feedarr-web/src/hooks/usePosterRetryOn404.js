import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { apiPost, resolveApiUrl } from "../api/client.js";

const RETRY_DELAYS_MS = [2000, 4000, 8000, 15000, 30000, 60000, 120000, 180000, 300000, 600000];
const LOADER_MAX_MS = 20000;

function isLocalPosterUrl(url) {
  if (!url) return false;
  const raw = String(url);
  if (raw.startsWith("/api/posters/") || raw.startsWith("/posters/")) return true;
  if (/^https?:\/\//i.test(raw)) {
    try {
      const parsed = new URL(raw);
      return parsed.pathname.startsWith("/api/posters/") || parsed.pathname.startsWith("/posters/");
    } catch {
      return false;
    }
  }
  return false;
}

function appendCacheBust(url, token) {
  if (!url || !token) return url;
  const sep = url.includes("?") ? "&" : "?";
  return `${url}${sep}cb=${token}`;
}

export default function usePosterRetryOn404(itemId, posterUrl) {
  const [cacheBust, setCacheBust] = useState(0);
  const [imgError, setImgError] = useState(false);
  const [refreshing, setRefreshing] = useState(false);
  const retryTimerRef = useRef(null);
  const refreshInFlightRef = useRef(false);
  const retryCountRef = useRef(0);
  const loaderTimerRef = useRef(null);
  const hasRefreshedRef = useRef(false);

  const basePosterUrl = useMemo(() => {
    if (posterUrl) return posterUrl;
    if (itemId) return `/api/posters/release/${itemId}`;
    return "";
  }, [itemId, posterUrl]);

  const posterSrc = useMemo(() => {
    const withBust = appendCacheBust(basePosterUrl, cacheBust);
    return resolveApiUrl(withBust);
  }, [basePosterUrl, cacheBust]);

  const clearTimers = useCallback(() => {
    if (retryTimerRef.current) {
      clearTimeout(retryTimerRef.current);
      retryTimerRef.current = null;
    }
    if (loaderTimerRef.current) {
      clearTimeout(loaderTimerRef.current);
      loaderTimerRef.current = null;
    }
  }, []);

  useEffect(() => {
    clearTimers();
    setImgError(false);
    setRefreshing(false);
    setCacheBust(0);
    refreshInFlightRef.current = false;
    retryCountRef.current = 0;
    hasRefreshedRef.current = false;
  }, [basePosterUrl, clearTimers]);

  useEffect(() => () => clearTimers(), [clearTimers]);

  const handleImageLoad = useCallback(() => {
    setImgError(false);
    if (refreshing) setRefreshing(false);
    if (loaderTimerRef.current) {
      clearTimeout(loaderTimerRef.current);
      loaderTimerRef.current = null;
    }
  }, [refreshing]);

  const handleImageError = useCallback(async () => {
    setImgError(true);
    if (!itemId || !basePosterUrl) return;
    if (!isLocalPosterUrl(basePosterUrl)) return;
    if (!retryTimerRef.current && retryCountRef.current < RETRY_DELAYS_MS.length) {
      if (!loaderTimerRef.current) {
        loaderTimerRef.current = setTimeout(() => {
          loaderTimerRef.current = null;
          setRefreshing(false);
        }, LOADER_MAX_MS);
      }
      setRefreshing(true);
    }

    if (!hasRefreshedRef.current && !refreshInFlightRef.current) {
      refreshInFlightRef.current = true;

      let shouldRefresh = false;
      try {
        const head = await fetch(resolveApiUrl(basePosterUrl), {
          method: "HEAD",
          cache: "no-store",
          credentials: "include",
        });
        shouldRefresh = head.status === 404;
      } catch {
        shouldRefresh = false;
      }

      if (shouldRefresh) {
        hasRefreshedRef.current = true;
        try {
          await apiPost(`/api/posters/${itemId}/refresh`);
        } catch {
          // ignore refresh errors and keep retrying
        }
      }

      refreshInFlightRef.current = false;
    }

    if (retryTimerRef.current) return;
    if (retryCountRef.current >= RETRY_DELAYS_MS.length) {
      if (loaderTimerRef.current) {
        clearTimeout(loaderTimerRef.current);
        loaderTimerRef.current = null;
      }
      setRefreshing(false);
      return;
    }

    const delay = RETRY_DELAYS_MS[retryCountRef.current];
    retryCountRef.current += 1;

    retryTimerRef.current = setTimeout(() => {
      retryTimerRef.current = null;
      setCacheBust(Date.now());
      setImgError(false);
    }, delay);
  }, [basePosterUrl, itemId]);

  return {
    posterSrc,
    imgError,
    refreshing,
    handleImageError,
    handleImageLoad,
  };
}
