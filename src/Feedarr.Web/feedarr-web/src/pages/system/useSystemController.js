import { useCallback, useEffect, useMemo, useState } from "react";
import { apiGet } from "../../api/client.js";
import useUpdates from "./hooks/useUpdates.js";

const DEFAULT_EXTERNAL = {
  hasTmdbApiKey: false,
  hasFanartApiKey: false,
  hasIgdbClientId: false,
  hasIgdbClientSecret: false,
};

const DEFAULT_PROVIDER_STATS = {};

function toHasFlagName(fieldKey) {
  const raw = String(fieldKey || "").trim();
  if (!raw) return "";
  return `has${raw.charAt(0).toUpperCase()}${raw.slice(1)}`;
}

function hasRequiredAuth(instance, definition) {
  if (!definition) return false;
  const required = (definition.fieldsSchema || []).filter((field) => field.required);
  if (required.length === 0) return true;
  const flags = instance?.authFlags || {};
  return required.every((field) => !!flags[toHasFlagName(field.key)]);
}

const PROVIDER_LABELS = {
  tmdb: "TMDB",
  tvmaze: "TVmaze",
  fanart: "Fanart.tv",
  igdb: "IGDB",
  jikan: "Jikan (MAL)",
  googlebooks: "Google Books",
  theaudiodb: "TheAudioDB",
  comicvine: "Comic Vine",
};

const DEFAULT_STORAGE_INFO = { volumes: [], usage: {} };

function createDefaultExternal() {
  return { ...DEFAULT_EXTERNAL };
}

function createDefaultProviderStats() {
  return { ...DEFAULT_PROVIDER_STATS };
}

function createDefaultStorageInfo() {
  return { ...DEFAULT_STORAGE_INFO, usage: { ...DEFAULT_STORAGE_INFO.usage } };
}

export default function useSystemController(section = "overview") {
  const showStorage = section === "storage";
  const showProviders = section === "providers" || section === "externals";
  const showOverview = section === "overview";
  const showStatistics = section === "statistics";
  const showUpdates = section === "updates" || section === "about";
  const systemTitleBySection = {
    overview: "Système",
    storage: "Stockage",
    providers: "Métadonnées",
    externals: "Métadonnées",
    statistics: "Statistiques",
    indexers: "Fournisseurs",
    updates: "À propos",
    about: "À propos",
  };
  const systemTitle = systemTitleBySection[section] || "Système";

  const [loading, setLoading] = useState(true);
  const [err, setErr] = useState("");
  const [status, setStatus] = useState(null);
  const [sources, setSources] = useState([]);
  const [external, setExternal] = useState(createDefaultExternal);
  const [externalProviders, setExternalProviders] = useState({ definitions: [], instances: [] });
  const [providerStats, setProviderStats] = useState(createDefaultProviderStats);
  const [missingPosterCount, setMissingPosterCount] = useState(0);
  const [storageInfo, setStorageInfo] = useState(createDefaultStorageInfo);
  const updates = useUpdates();

  const load = useCallback(async () => {
    setLoading(true);
    setErr("");

    const [sysRes, srcRes, extRes, provRes, missingRes, extProvidersRes] = await Promise.allSettled([
      apiGet("/api/system/status"),
      apiGet("/api/sources"),
      apiGet("/api/settings/external"),
      apiGet("/api/system/providers"),
      apiGet("/api/posters/missing-count"),
      apiGet("/api/providers/external"),
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
      errors.push("Métadonnées indisponibles");
    }

    if (provRes.status === "fulfilled") {
      const nextStats = {};
      const payload = provRes.value;
      if (payload && typeof payload === "object") {
        Object.entries(payload).forEach(([providerKey, value]) => {
          if (!value || typeof value !== "object") return;
          nextStats[String(providerKey).toLowerCase()] = {
            calls: Number(value.calls ?? 0),
            failures: Number(value.failures ?? 0),
            avgMs: Number(value.avgMs ?? 0),
          };
        });
      }
      setProviderStats(nextStats);
    } else {
      setProviderStats(createDefaultProviderStats());
      errors.push("Stats métadonnées indisponibles");
    }

    if (extProvidersRes.status === "fulfilled") {
      const definitions = Array.isArray(extProvidersRes.value?.definitions) ? extProvidersRes.value.definitions : [];
      const instances = Array.isArray(extProvidersRes.value?.instances) ? extProvidersRes.value.instances : [];
      setExternalProviders({ definitions, instances });
    } else {
      setExternalProviders({ definitions: [], instances: [] });
      errors.push("Configuration metadata indisponible");
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

  const metadataProviderRows = useMemo(() => {
    const definitions = Array.isArray(externalProviders?.definitions) ? externalProviders.definitions : [];
    const instances = (Array.isArray(externalProviders?.instances) ? externalProviders.instances : [])
      .filter((instance) => instance?.enabled !== false);
    const definitionByKey = new Map(
      definitions.map((definition) => [String(definition?.providerKey || "").toLowerCase(), definition])
    );

    return instances.map((instance) => {
      const providerKey = String(instance?.providerKey || "").toLowerCase();
      const definition = definitionByKey.get(providerKey);
      const requiredFields = (definition?.fieldsSchema || []).filter((field) => field.required);
      const configured = hasRequiredAuth(instance, definition);
      const stats = providerStats?.[providerKey] || { calls: 0, failures: 0, avgMs: 0 };

      const apiStatus = requiredFields.length === 0
        ? "N/A"
        : (configured ? "OK" : "NO");

      return {
        instanceId: String(instance?.instanceId || providerKey),
        providerKey,
        label: instance?.displayName || definition?.displayName || PROVIDER_LABELS[providerKey] || providerKey.toUpperCase(),
        apiStatus,
        apiStatusClass: apiStatus === "OK" ? "ok" : "warn",
        calls: Number(stats.calls ?? 0),
        failures: Number(stats.failures ?? 0),
        avgMs: Number(stats.avgMs ?? 0),
      };
    });
  }, [externalProviders, providerStats]);

  return {
    systemTitle,
    showStorage,
    showProviders,
    showOverview,
    showStatistics,
    showUpdates,
    loading,
    err,
    status,
    sources,
    external,
    providerStats,
    metadataProviderRows,
    missingPosterCount,
    storageInfo,
    load,
    matchingPercent,
    matchingColor,
    updates: {
      updatesLoading: updates.loading,
      updatesChecking: updates.checking,
      updatesError: updates.error,
      updatesEnabled: updates.updatesEnabled,
      currentVersion: updates.currentVersion,
      isUpdateAvailable: updates.isUpdateAvailable,
      latestRelease: updates.latestRelease,
      releases: updates.releases,
      hasUnseenUpdate: updates.hasUnseenUpdate,
      checkIntervalHours: updates.checkIntervalHours,
      checkForUpdates: (force = false) => updates.checkForUpdates({ force }),
      acknowledgeLatest: updates.acknowledgeLatest,
    },
  };
}
