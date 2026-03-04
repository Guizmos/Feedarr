import { useCallback, useEffect, useRef, useState } from "react";
import { apiGet, apiPut } from "../../../api/client.js";

export const defaultMaintenanceSettings = {
  maintenanceAdvancedOptionsEnabled: false,
  syncSourcesMaxConcurrency: 2,
  posterWorkers: 1,
  providerRateLimitMode: "auto",
  providerConcurrencyManual: {
    tmdb: 2,
    igdb: 1,
    fanart: 1,
    tvmaze: 1,
    others: 1,
  },
  syncRunTimeoutMinutes: 10,
  configuredProviders: null,
};

function clamp(value, min, max, fallback) {
  const numeric = Number(value);
  if (!Number.isFinite(numeric)) return fallback;
  return Math.max(min, Math.min(max, Math.trunc(numeric)));
}

export function normalizeProviderRateLimitMode(value) {
  return String(value || "").trim().toLowerCase() === "manual" ? "manual" : "auto";
}

export function buildMaintenancePayload(source, overrides = {}) {
  const merged = { ...source, ...overrides };
  const manualSource = {
    ...defaultMaintenanceSettings.providerConcurrencyManual,
    ...(source?.providerConcurrencyManual || {}),
    ...(overrides?.providerConcurrencyManual || {}),
  };

  return {
    maintenanceAdvancedOptionsEnabled: !!merged.maintenanceAdvancedOptionsEnabled,
    syncSourcesMaxConcurrency: clamp(merged.syncSourcesMaxConcurrency, 1, 4, defaultMaintenanceSettings.syncSourcesMaxConcurrency),
    posterWorkers: clamp(merged.posterWorkers, 1, 2, defaultMaintenanceSettings.posterWorkers),
    providerRateLimitMode: normalizeProviderRateLimitMode(merged.providerRateLimitMode),
    providerConcurrencyManual: {
      tmdb: clamp(manualSource.tmdb, 1, 3, defaultMaintenanceSettings.providerConcurrencyManual.tmdb),
      igdb: clamp(manualSource.igdb, 1, 2, defaultMaintenanceSettings.providerConcurrencyManual.igdb),
      fanart: clamp(manualSource.fanart, 1, 2, defaultMaintenanceSettings.providerConcurrencyManual.fanart),
      tvmaze: clamp(manualSource.tvmaze, 1, 2, defaultMaintenanceSettings.providerConcurrencyManual.tvmaze),
      others: clamp(manualSource.others, 1, 2, defaultMaintenanceSettings.providerConcurrencyManual.others),
    },
    syncRunTimeoutMinutes: clamp(merged.syncRunTimeoutMinutes, 3, 30, defaultMaintenanceSettings.syncRunTimeoutMinutes),
  };
}

export function normalizeConfiguredProviders(value) {
  if (!Array.isArray(value)) return null;

  const normalized = value
    .map((item) => String(item || "").trim().toLowerCase())
    .filter(Boolean);

  return [...new Set(normalized)];
}

export function buildMaintenanceState(source, overrides = {}) {
  const payload = buildMaintenancePayload(source, overrides);
  const configuredProviders = Object.prototype.hasOwnProperty.call(source || {}, "configuredProviders")
    || Object.prototype.hasOwnProperty.call(overrides || {}, "configuredProviders")
    ? normalizeConfiguredProviders(overrides?.configuredProviders ?? source?.configuredProviders)
    : null;

  return {
    ...payload,
    configuredProviders,
  };
}

export function getVisibleMaintenanceProviderRows(settings, providerRows) {
  const configuredProviders = Array.isArray(settings?.configuredProviders)
    ? settings.configuredProviders
    : null;

  if (configuredProviders == null) return providerRows;
  if (configuredProviders.length === 0) return [];

  return providerRows.filter((provider) => provider.alwaysVisible || configuredProviders.includes(provider.key));
}

export function getMaintenancePerformanceNotice(source) {
  const settings = buildMaintenancePayload(source);

  if (settings.posterWorkers === 2 && settings.providerRateLimitMode !== "auto") {
    return {
      tone: "danger",
      title: "Risque élevé",
      message: "Deux workers posters avec des limites manuelles augmentent fortement le risque de 429, bannissement temporaire ou timeouts.",
    };
  }

  if (settings.syncSourcesMaxConcurrency >= 3) {
    return {
      tone: "warning",
      title: "Charge accrue",
      message: "La sync peut solliciter davantage SQLite, le réseau et le disque. Sur NAS ou stockage lent, surveille la latence.",
    };
  }

  if (settings.posterWorkers === 2) {
    return {
      tone: "warning",
      title: "À surveiller",
      message: "Deux workers posters restent raisonnables, mais surveille les rate limits et les temps de réponse des providers.",
    };
  }

  if (settings.syncSourcesMaxConcurrency === 1) {
    return {
      tone: "info",
      title: "Très stable",
      message: "La charge reste minimale, mais la sync sera plus lente et peut dériver davantage sur les longues séries de sources.",
    };
  }

  return {
    tone: "ok",
    title: "Réglage recommandé",
    message: "Équilibre correct entre vitesse, stabilité et pression réseau. C'est le réglage conseillé pour la plupart des instances.",
  };
}

function collectChangedKeys(current, initial) {
  const changed = new Set();

  if (!!current.maintenanceAdvancedOptionsEnabled !== !!initial.maintenanceAdvancedOptionsEnabled) changed.add("maintenance.maintenanceAdvancedOptionsEnabled");
  if (Number(current.syncSourcesMaxConcurrency) !== Number(initial.syncSourcesMaxConcurrency)) changed.add("maintenance.syncSourcesMaxConcurrency");
  if (Number(current.posterWorkers) !== Number(initial.posterWorkers)) changed.add("maintenance.posterWorkers");
  if (normalizeProviderRateLimitMode(current.providerRateLimitMode) !== normalizeProviderRateLimitMode(initial.providerRateLimitMode)) changed.add("maintenance.providerRateLimitMode");
  if (Number(current.providerConcurrencyManual?.tmdb) !== Number(initial.providerConcurrencyManual?.tmdb)) changed.add("maintenance.providerConcurrencyManual.tmdb");
  if (Number(current.providerConcurrencyManual?.igdb) !== Number(initial.providerConcurrencyManual?.igdb)) changed.add("maintenance.providerConcurrencyManual.igdb");
  if (Number(current.providerConcurrencyManual?.fanart) !== Number(initial.providerConcurrencyManual?.fanart)) changed.add("maintenance.providerConcurrencyManual.fanart");
  if (Number(current.providerConcurrencyManual?.tvmaze) !== Number(initial.providerConcurrencyManual?.tvmaze)) changed.add("maintenance.providerConcurrencyManual.tvmaze");
  if (Number(current.providerConcurrencyManual?.others) !== Number(initial.providerConcurrencyManual?.others)) changed.add("maintenance.providerConcurrencyManual.others");
  if (Number(current.syncRunTimeoutMinutes) !== Number(initial.syncRunTimeoutMinutes)) changed.add("maintenance.syncRunTimeoutMinutes");

  return changed;
}

function normalizeValidationErrors(error) {
  const raw = error?.extensions?.errors || error?.payload?.errors || {};
  const normalized = {};
  Object.entries(raw).forEach(([key, value]) => {
    if (Array.isArray(value)) {
      normalized[key] = value[0] || "";
    } else if (typeof value === "string") {
      normalized[key] = value;
    }
  });
  return normalized;
}

function markMaintenanceSettingsError(error) {
  if (error && typeof error === "object") {
    error.isMaintenanceSettingsError = true;
    return error;
  }

  const wrapped = new Error(String(error || "Maintenance settings error"));
  wrapped.isMaintenanceSettingsError = true;
  return wrapped;
}

export default function useMaintenanceSettings() {
  const [maintenanceSettings, setMaintenanceSettings] = useState(defaultMaintenanceSettings);
  const [initialMaintenanceSettings, setInitialMaintenanceSettings] = useState(defaultMaintenanceSettings);
  const [fieldErrors, setFieldErrors] = useState({});
  const [saveError, setSaveError] = useState("");
  const [pulseKinds, setPulseKinds] = useState({});
  const pulseTimerRef = useRef(null);

  const isDirty =
    JSON.stringify(buildMaintenancePayload(maintenanceSettings)) !==
    JSON.stringify(buildMaintenancePayload(initialMaintenanceSettings));

  const loadMaintenanceSettings = useCallback(async () => {
    try {
      const data = await apiGet("/api/settings/maintenance");
      const normalized = buildMaintenanceState(data || defaultMaintenanceSettings);
      setMaintenanceSettings(normalized);
      setInitialMaintenanceSettings(normalized);
      setFieldErrors({});
      setSaveError("");
    } catch {
      // Ignore initial load failures and keep safe defaults.
    }
  }, []);

  const applyPulse = useCallback((keys, kind) => {
    if (pulseTimerRef.current) clearTimeout(pulseTimerRef.current);

    const next = {};
    [...keys].forEach((key) => {
      next[key] = kind;
    });
    setPulseKinds(next);

    pulseTimerRef.current = setTimeout(() => {
      setPulseKinds({});
    }, 1200);
  }, []);

  const saveMaintenanceSettings = useCallback(async () => {
    const changed = collectChangedKeys(maintenanceSettings, initialMaintenanceSettings);
    if (changed.size === 0) return changed;

    setFieldErrors({});
    setSaveError("");

    try {
      const saved = await apiPut("/api/settings/maintenance", buildMaintenancePayload(maintenanceSettings));
      const normalized = buildMaintenanceState(saved || maintenanceSettings, {
        configuredProviders: saved?.configuredProviders ?? maintenanceSettings?.configuredProviders ?? null,
      });
      setMaintenanceSettings(normalized);
      setInitialMaintenanceSettings(normalized);
      applyPulse(changed, "ok");
      return changed;
    } catch (error) {
      const normalizedErrors = normalizeValidationErrors(error);
      setFieldErrors(normalizedErrors);
      setSaveError(error?.message || "Erreur sauvegarde options avancées");
      applyPulse(changed.size > 0 ? changed : new Set(Object.keys(normalizedErrors)), "err");
      throw markMaintenanceSettingsError(error);
    }
  }, [applyPulse, initialMaintenanceSettings, maintenanceSettings]);

  const restoreRecommendedDefaults = useCallback(() => {
    setMaintenanceSettings((current) => buildMaintenanceState(current, defaultMaintenanceSettings));
    setFieldErrors({});
    setSaveError("");
  }, []);

  const toggleAdvancedOptions = useCallback(() => {
    setMaintenanceSettings((current) => ({
      ...current,
      maintenanceAdvancedOptionsEnabled: !current.maintenanceAdvancedOptionsEnabled,
    }));
  }, []);

  useEffect(() => {
    return () => {
      if (pulseTimerRef.current) clearTimeout(pulseTimerRef.current);
    };
  }, []);

  return {
    maintenanceSettings,
    setMaintenanceSettings,
    initialMaintenanceSettings,
    isDirty,
    fieldErrors,
    saveError,
    pulseKinds,
    loadMaintenanceSettings,
    saveMaintenanceSettings,
    restoreRecommendedDefaults,
    toggleAdvancedOptions,
  };
}
