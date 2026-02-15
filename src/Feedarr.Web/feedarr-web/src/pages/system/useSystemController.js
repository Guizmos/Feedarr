import { useCallback, useEffect, useMemo, useState } from "react";
import { apiGet } from "../../api/client.js";

const DEFAULT_EXTERNAL = {
  hasTmdbApiKey: false,
  hasFanartApiKey: false,
  hasIgdbClientId: false,
  hasIgdbClientSecret: false,
};

const DEFAULT_PROVIDER_STATS = {
  tmdb: { calls: 0, failures: 0, avgMs: 0 },
  tvmaze: { calls: 0, failures: 0, avgMs: 0 },
  fanart: { calls: 0, failures: 0, avgMs: 0 },
  igdb: { calls: 0, failures: 0, avgMs: 0 },
};

const DEFAULT_STORAGE_INFO = { volumes: [], usage: {} };

function createDefaultExternal() {
  return { ...DEFAULT_EXTERNAL };
}

function createDefaultProviderStats() {
  return {
    tmdb: { ...DEFAULT_PROVIDER_STATS.tmdb },
    tvmaze: { ...DEFAULT_PROVIDER_STATS.tvmaze },
    fanart: { ...DEFAULT_PROVIDER_STATS.fanart },
    igdb: { ...DEFAULT_PROVIDER_STATS.igdb },
  };
}

function createDefaultStorageInfo() {
  return { ...DEFAULT_STORAGE_INFO, usage: { ...DEFAULT_STORAGE_INFO.usage } };
}

export default function useSystemController(section = "overview") {
  const showStorage = section === "storage";
  const showProviders = section === "providers" || section === "externals";
  const showOverview = section === "overview";
  const showStatistics = section === "statistics";
  const systemTitleBySection = {
    overview: "Système",
    storage: "Stockage",
    providers: "Providers",
    externals: "Providers",
    statistics: "Statistiques",
    indexers: "Indexeurs",
  };
  const systemTitle = systemTitleBySection[section] || "Système";

  const [loading, setLoading] = useState(true);
  const [err, setErr] = useState("");
  const [status, setStatus] = useState(null);
  const [sources, setSources] = useState([]);
  const [external, setExternal] = useState(createDefaultExternal);
  const [providerStats, setProviderStats] = useState(createDefaultProviderStats);
  const [missingPosterCount, setMissingPosterCount] = useState(0);
  const [storageInfo, setStorageInfo] = useState(createDefaultStorageInfo);

  const load = useCallback(async () => {
    setLoading(true);
    setErr("");

    const [sysRes, srcRes, extRes, provRes, missingRes] = await Promise.allSettled([
      apiGet("/api/system/status"),
      apiGet("/api/sources"),
      apiGet("/api/settings/external"),
      apiGet("/api/system/providers"),
      apiGet("/api/posters/missing-count"),
    ]);

    const errors = [];

    if (sysRes.status === "fulfilled") {
      setStatus(sysRes.value || null);
    } else {
      setStatus(null);
      errors.push("Statut système indisponible");
    }

    if (srcRes.status === "fulfilled") {
      setSources(Array.isArray(srcRes.value) ? srcRes.value : []);
    } else {
      setSources([]);
      errors.push("Sources indisponibles");
    }

    if (extRes.status === "fulfilled") {
      setExternal({
        hasTmdbApiKey: !!extRes.value?.hasTmdbApiKey,
        hasFanartApiKey: !!extRes.value?.hasFanartApiKey,
        hasIgdbClientId: !!extRes.value?.hasIgdbClientId,
        hasIgdbClientSecret: !!extRes.value?.hasIgdbClientSecret,
      });
    } else {
      setExternal(createDefaultExternal());
      errors.push("Providers indisponibles");
    }

    if (provRes.status === "fulfilled") {
      setProviderStats({
        tmdb: {
          calls: Number(provRes.value?.tmdb?.calls ?? 0),
          failures: Number(provRes.value?.tmdb?.failures ?? 0),
          avgMs: Number(provRes.value?.tmdb?.avgMs ?? 0),
        },
        tvmaze: {
          calls: Number(provRes.value?.tvmaze?.calls ?? 0),
          failures: Number(provRes.value?.tvmaze?.failures ?? 0),
          avgMs: Number(provRes.value?.tvmaze?.avgMs ?? 0),
        },
        fanart: {
          calls: Number(provRes.value?.fanart?.calls ?? 0),
          failures: Number(provRes.value?.fanart?.failures ?? 0),
          avgMs: Number(provRes.value?.fanart?.avgMs ?? 0),
        },
        igdb: {
          calls: Number(provRes.value?.igdb?.calls ?? 0),
          failures: Number(provRes.value?.igdb?.failures ?? 0),
          avgMs: Number(provRes.value?.igdb?.avgMs ?? 0),
        },
      });
    } else {
      setProviderStats(createDefaultProviderStats());
      errors.push("Stats providers indisponibles");
    }

    if (missingRes.status === "fulfilled") {
      setMissingPosterCount(Math.max(0, Number(missingRes.value?.count ?? 0)));
    } else {
      setMissingPosterCount(0);
    }

    if (errors.length) setErr(errors.join(" • "));
    setLoading(false);
  }, []);

  const loadStorage = useCallback(async () => {
    try {
      const data = await apiGet("/api/system/storage");
      if (data) setStorageInfo(data);
    } catch {
      setStorageInfo(createDefaultStorageInfo());
    }
  }, []);

  useEffect(() => {
    load();
  }, [load]);

  useEffect(() => {
    if (!showStorage) return;
    loadStorage();
  }, [showStorage, loadStorage]);

  const releasesCount = useMemo(() => {
    return Number(status?.releasesCount ?? status?.ReleasesCount ?? 0);
  }, [status]);

  const matchingPercent = useMemo(() => {
    const raw =
      releasesCount > 0
        ? Math.round(((releasesCount - (missingPosterCount || 0)) / releasesCount) * 100)
        : 0;
    return Math.max(0, Math.min(100, raw));
  }, [releasesCount, missingPosterCount]);

  const matchingColor = useMemo(() => {
    if (matchingPercent <= 50) return "#ef4444";
    if (matchingPercent <= 60) return "#f59e0b";
    if (matchingPercent <= 70) return "#3b82f6";
    return "#22c55e";
  }, [matchingPercent]);

  return {
    systemTitle,
    showStorage,
    showProviders,
    showOverview,
    showStatistics,
    loading,
    err,
    status,
    sources,
    external,
    providerStats,
    missingPosterCount,
    storageInfo,
    load,
    matchingPercent,
    matchingColor,
  };
}
