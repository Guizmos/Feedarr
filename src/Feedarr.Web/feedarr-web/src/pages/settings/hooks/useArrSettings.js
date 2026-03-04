import { useCallback, useEffect, useRef, useState } from "react";
import { apiGet, apiPut } from "../../../api/client.js";
import { normalizeRequestMode } from "../../../utils/appTypes.js";

export const defaultArrSettings = {
  arrSyncIntervalMinutes: 60,
  arrAutoSyncEnabled: true,
  requestIntegrationMode: "arr",
};

function clampInterval(value) {
  const parsed = Number(value);
  if (!Number.isFinite(parsed)) return defaultArrSettings.arrSyncIntervalMinutes;
  return Math.max(1, Math.min(1440, Math.trunc(parsed)));
}

export function buildArrPayload(source, overrides = {}) {
  const merged = { ...defaultArrSettings, ...source, ...overrides };
  return {
    arrSyncIntervalMinutes: clampInterval(merged.arrSyncIntervalMinutes),
    arrAutoSyncEnabled: merged.arrAutoSyncEnabled == null ? true : !!merged.arrAutoSyncEnabled,
    requestIntegrationMode: normalizeRequestMode(merged.requestIntegrationMode),
  };
}

export function normalizeArrResponse(source) {
  return buildArrPayload(source);
}

export function collectChangedArrKeys(current, initial) {
  const changed = new Set();

  if (Number(current.arrSyncIntervalMinutes) !== Number(initial.arrSyncIntervalMinutes)) {
    changed.add("arr.arrSyncIntervalMinutes");
  }
  if (!!current.arrAutoSyncEnabled !== !!initial.arrAutoSyncEnabled) {
    changed.add("arr.arrAutoSyncEnabled");
  }
  if (current.requestIntegrationMode !== initial.requestIntegrationMode) {
    changed.add("arr.requestIntegrationMode");
  }

  return changed;
}

export function normalizeArrValidationErrors(error) {
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

export async function loadArrSettingsData(request = apiGet) {
  const response = await request("/api/settings/arr");
  return normalizeArrResponse(response || defaultArrSettings);
}

export async function saveArrSettingsData(settings, request = apiPut) {
  const response = await request("/api/settings/arr", buildArrPayload(settings));
  return normalizeArrResponse(response || settings);
}

function markArrSettingsError(error) {
  if (error && typeof error === "object") {
    error.isArrSettingsError = true;
    return error;
  }

  const wrapped = new Error(String(error || "ARR settings error"));
  wrapped.isArrSettingsError = true;
  return wrapped;
}

export default function useArrSettings() {
  const [arrSettings, setArrSettings] = useState(defaultArrSettings);
  const [initialArrSettings, setInitialArrSettings] = useState(defaultArrSettings);
  const [requestModeDraft, setRequestModeDraft] = useState(defaultArrSettings.requestIntegrationMode);
  const [loading, setLoading] = useState(false);
  const [loadError, setLoadError] = useState("");
  const [fieldErrors, setFieldErrors] = useState({});
  const [saveError, setSaveError] = useState("");
  const [saving, setSaving] = useState(false);
  const [pulseKinds, setPulseKinds] = useState({});
  const pulseTimerRef = useRef(null);

  const mergedCurrent = buildArrPayload(arrSettings, {
    requestIntegrationMode: requestModeDraft,
  });
  const mergedInitial = buildArrPayload(initialArrSettings);
  const isDirty = JSON.stringify(mergedCurrent) !== JSON.stringify(mergedInitial);
  const isRequestModeDirty = requestModeDraft !== mergedInitial.requestIntegrationMode;

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

  const loadArrSettings = useCallback(async () => {
    setLoading(true);
    setLoadError("");

    try {
      const loaded = await loadArrSettingsData();
      setArrSettings(loaded);
      setInitialArrSettings(loaded);
      setRequestModeDraft(loaded.requestIntegrationMode);
      setFieldErrors({});
      setSaveError("");
      return loaded;
    } catch (error) {
      setLoadError(error?.message || "Erreur chargement paramètres ARR");
      throw error;
    } finally {
      setLoading(false);
    }
  }, []);

  const saveArrSettings = useCallback(async (nextSettings) => {
    const payload = buildArrPayload(
      nextSettings || arrSettings,
      nextSettings ? {} : { requestIntegrationMode: requestModeDraft },
    );
    const changed = collectChangedArrKeys(payload, mergedInitial);
    if (changed.size === 0) return changed;

    setSaving(true);
    setFieldErrors({});
    setSaveError("");

    try {
      const saved = await saveArrSettingsData(payload);
      setArrSettings(saved);
      setInitialArrSettings(saved);
      setRequestModeDraft(saved.requestIntegrationMode);
      applyPulse(changed, "ok");
      return changed;
    } catch (error) {
      const normalizedErrors = normalizeArrValidationErrors(error);
      setFieldErrors(normalizedErrors);
      setSaveError(error?.message || "Erreur sauvegarde paramètres ARR");
      const fallbackKeys = Object.keys(normalizedErrors).map((key) => `arr.${key}`);
      applyPulse(changed.size > 0 ? changed : new Set(fallbackKeys), "err");
      throw markArrSettingsError(error);
    } finally {
      setSaving(false);
    }
  }, [applyPulse, arrSettings, mergedInitial, requestModeDraft]);

  const setArrSettingsState = useCallback((updater) => {
    setSaveError("");
    setArrSettings((current) => {
      const next = typeof updater === "function" ? updater(current) : updater;
      return buildArrPayload(next);
    });
    setFieldErrors((current) => {
      if (
        !Object.prototype.hasOwnProperty.call(current, "arrSyncIntervalMinutes")
        && !Object.prototype.hasOwnProperty.call(current, "arrAutoSyncEnabled")
      ) {
        return current;
      }

      const next = { ...current };
      delete next.arrSyncIntervalMinutes;
      delete next.arrAutoSyncEnabled;
      return next;
    });
  }, []);

  const setRequestModeDraftState = useCallback((value) => {
    setSaveError("");
    setRequestModeDraft(normalizeRequestMode(value));
    setFieldErrors((current) => {
      if (!Object.prototype.hasOwnProperty.call(current, "requestIntegrationMode")) return current;
      const next = { ...current };
      delete next.requestIntegrationMode;
      return next;
    });
  }, []);

  useEffect(() => {
    return () => {
      if (pulseTimerRef.current) clearTimeout(pulseTimerRef.current);
    };
  }, []);

  return {
    arrSettings,
    setArrSettings: setArrSettingsState,
    initialArrSettings,
    requestModeDraft,
    setRequestModeDraft: setRequestModeDraftState,
    loading,
    loadError,
    fieldErrors,
    saveError,
    saving,
    pulseKeys: new Set(Object.keys(pulseKinds)),
    pulseKinds,
    isDirty,
    isRequestModeDirty,
    loadArrSettings,
    saveArrSettings,
  };
}
