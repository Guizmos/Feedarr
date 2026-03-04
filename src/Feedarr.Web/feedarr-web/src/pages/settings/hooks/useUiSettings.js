import { useCallback, useEffect, useRef, useState } from "react";
import { apiGet, apiPut } from "../../../api/client.js";
import { applyTheme, getStoredTheme } from "../../../app/theme.js";
import {
  applyUiLanguage,
  DEFAULT_MEDIA_INFO_LANGUAGE,
  DEFAULT_UI_LANGUAGE,
  getStoredUiLanguage,
  normalizeMediaInfoLanguage,
  normalizeUiLanguage,
} from "../../../app/locale.js";

export const defaultUi = {
  uiLanguage: DEFAULT_UI_LANGUAGE,
  mediaInfoLanguage: DEFAULT_MEDIA_INFO_LANGUAGE,
  hideSeenByDefault: false,
  showCategories: true,
  showTop24DedupeControl: false,
  defaultView: "grid",
  defaultSort: "date",
  defaultMaxAgeDays: "",
  defaultLimit: 100,
  defaultFilterSeen: "",
  defaultFilterApplication: "",
  defaultFilterSourceId: "",
  defaultFilterCategoryId: "",
  defaultFilterQuality: "",
  badgeInfo: false,
  badgeWarn: true,
  badgeError: true,
  theme: "light",
  enableMissingPosterView: false,
  animationsEnabled: true,
  onboardingDone: false,
};

function normalizeUiModel(source, overrides = {}) {
  const merged = { ...defaultUi, ...source, ...overrides };
  return {
    uiLanguage: normalizeUiLanguage(merged.uiLanguage),
    mediaInfoLanguage: normalizeMediaInfoLanguage(merged.mediaInfoLanguage),
    hideSeenByDefault: !!merged.hideSeenByDefault,
    showCategories: !!merged.showCategories,
    showTop24DedupeControl: !!merged.showTop24DedupeControl,
    defaultView: merged.defaultView || "grid",
    defaultSort: merged.defaultSort || "date",
    defaultMaxAgeDays: merged.defaultMaxAgeDays ?? "",
    defaultLimit: merged.defaultLimit ?? 100,
    defaultFilterSeen: merged.defaultFilterSeen ?? "",
    defaultFilterApplication: merged.defaultFilterApplication ?? "",
    defaultFilterSourceId: merged.defaultFilterSourceId ?? "",
    defaultFilterCategoryId: merged.defaultFilterCategoryId ?? "",
    defaultFilterQuality: merged.defaultFilterQuality ?? "",
    badgeInfo: !!merged.badgeInfo,
    badgeWarn: !!merged.badgeWarn,
    badgeError: !!merged.badgeError,
    theme: merged.theme || "light",
    enableMissingPosterView: !!merged.enableMissingPosterView,
    animationsEnabled: merged.animationsEnabled !== false,
    onboardingDone: !!merged.onboardingDone,
  };
}

export function buildUiPayload(source, overrides = {}) {
  return normalizeUiModel(source, overrides);
}

function normalizeOptionItem(item) {
  const value = String(item?.value ?? "").trim();
  const label = String(item?.label ?? "").trim();
  if (!value || !label) return null;
  return { value, label };
}

function normalizeCategoryOptionItem(item) {
  const option = normalizeOptionItem(item);
  if (!option) return null;
  return {
    ...option,
    count: Math.max(0, Math.trunc(Number(item?.count || 0))),
  };
}

export function normalizeUiResponse(source) {
  return {
    ui: normalizeUiModel(source),
    sourceOptions: (Array.isArray(source?.sourceOptions) ? source.sourceOptions : [])
      .map(normalizeOptionItem)
      .filter(Boolean),
    appOptions: (Array.isArray(source?.appOptions) ? source.appOptions : [])
      .map(normalizeOptionItem)
      .filter(Boolean),
    categoryOptions: (Array.isArray(source?.categoryOptions) ? source.categoryOptions : [])
      .map(normalizeCategoryOptionItem)
      .filter(Boolean),
  };
}

export function collectChangedUiKeys(current, initial) {
  const changed = new Set();

  if (current.uiLanguage !== initial.uiLanguage) changed.add("ui.uiLanguage");
  if (current.mediaInfoLanguage !== initial.mediaInfoLanguage) changed.add("ui.mediaInfoLanguage");
  if (current.hideSeenByDefault !== initial.hideSeenByDefault) changed.add("ui.hideSeen");
  if (current.showCategories !== initial.showCategories) changed.add("ui.showCategories");
  if (current.showTop24DedupeControl !== initial.showTop24DedupeControl) changed.add("ui.top24DedupeControl");
  if (current.enableMissingPosterView !== initial.enableMissingPosterView) changed.add("ui.missingPosterView");
  if (current.defaultView !== initial.defaultView) changed.add("ui.defaultView");
  if (current.defaultSort !== initial.defaultSort) changed.add("ui.defaultSort");
  if (current.defaultMaxAgeDays !== initial.defaultMaxAgeDays) changed.add("ui.defaultMaxAgeDays");
  if (Number(current.defaultLimit) !== Number(initial.defaultLimit)) changed.add("ui.defaultLimit");
  if (current.defaultFilterSeen !== initial.defaultFilterSeen) changed.add("ui.defaultFilterSeen");
  if (current.defaultFilterApplication !== initial.defaultFilterApplication) changed.add("ui.defaultFilterApplication");
  if (current.defaultFilterSourceId !== initial.defaultFilterSourceId) changed.add("ui.defaultFilterSourceId");
  if (current.defaultFilterCategoryId !== initial.defaultFilterCategoryId) changed.add("ui.defaultFilterCategoryId");
  if (current.defaultFilterQuality !== initial.defaultFilterQuality) changed.add("ui.defaultFilterQuality");
  if (current.badgeInfo !== initial.badgeInfo) changed.add("ui.badgeInfo");
  if (current.badgeWarn !== initial.badgeWarn) changed.add("ui.badgeWarn");
  if (current.badgeError !== initial.badgeError) changed.add("ui.badgeError");
  if (current.theme !== initial.theme) changed.add("ui.theme");
  if (current.animationsEnabled !== initial.animationsEnabled) changed.add("ui.animations");
  if (current.onboardingDone !== initial.onboardingDone) changed.add("ui.onboardingDone");

  return changed;
}

export function normalizeUiValidationErrors(error) {
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

export async function loadUiSettingsData(request = apiGet) {
  const response = await request("/api/settings/ui");
  return normalizeUiResponse(response || defaultUi);
}

export async function saveUiSettingsData(ui, request = apiPut) {
  const response = await request("/api/settings/ui", buildUiPayload(ui));
  return normalizeUiResponse(response || buildUiPayload(ui));
}

function markUiSettingsError(error) {
  if (error && typeof error === "object") {
    error.isUiSettingsError = true;
    return error;
  }

  const wrapped = new Error(String(error || "UI settings error"));
  wrapped.isUiSettingsError = true;
  return wrapped;
}

export default function useUiSettings() {
  const storedTheme = getStoredTheme();
  const storedUiLanguage = getStoredUiLanguage();
  const initialState = normalizeUiModel(defaultUi, {
    theme: storedTheme,
    uiLanguage: storedUiLanguage,
  });

  const [ui, setUi] = useState(initialState);
  const [initialUi, setInitialUi] = useState(initialState);
  const [sourceOptions, setSourceOptions] = useState([]);
  const [appOptions, setAppOptions] = useState([]);
  const [categoryOptions, setCategoryOptions] = useState([]);
  const [loading, setLoading] = useState(false);
  const [loadError, setLoadError] = useState("");
  const [fieldErrors, setFieldErrors] = useState({});
  const [saveError, setSaveError] = useState("");
  const [pulseKinds, setPulseKinds] = useState({});
  const pulseTimerRef = useRef(null);

  const isDirty =
    JSON.stringify(buildUiPayload(ui)) !==
    JSON.stringify(buildUiPayload(initialUi));

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

  const loadUiSettings = useCallback(async () => {
    setLoading(true);
    setLoadError("");
    try {
      const loaded = await loadUiSettingsData();
      setUi(loaded.ui);
      setInitialUi(loaded.ui);
      setSourceOptions(loaded.sourceOptions);
      setAppOptions(loaded.appOptions);
      setCategoryOptions(loaded.categoryOptions);
      setFieldErrors({});
      setSaveError("");
      return loaded;
    } catch (error) {
      setLoadError(error?.message || "Erreur chargement paramètres UI");
      throw error;
    } finally {
      setLoading(false);
    }
  }, []);

  const saveUiSettings = useCallback(async () => {
    const changed = collectChangedUiKeys(buildUiPayload(ui), buildUiPayload(initialUi));
    if (changed.size === 0) return changed;

    setFieldErrors({});
    setSaveError("");

    try {
      const saved = await saveUiSettingsData(ui);
      setUi(saved.ui);
      setInitialUi(saved.ui);
      setSourceOptions(saved.sourceOptions);
      setAppOptions(saved.appOptions);
      setCategoryOptions(saved.categoryOptions);
      applyTheme(saved.ui.theme, true);
      applyUiLanguage(saved.ui.uiLanguage, true);
      applyPulse(changed, "ok");
      return changed;
    } catch (error) {
      const normalizedErrors = normalizeUiValidationErrors(error);
      setFieldErrors(normalizedErrors);
      setSaveError(error?.message || "Erreur sauvegarde paramètres UI");
      applyPulse(changed.size > 0 ? changed : new Set(Object.keys(normalizedErrors).map((key) => `ui.${key}`)), "err");
      throw markUiSettingsError(error);
    }
  }, [applyPulse, initialUi, ui]);

  const setUiField = useCallback((field, value) => {
    setUi((current) => ({ ...current, [field]: value }));
    setFieldErrors((current) => {
      if (!Object.prototype.hasOwnProperty.call(current, field)) return current;
      const next = { ...current };
      delete next[field];
      return next;
    });
  }, []);

  const handleThemeChange = useCallback((nextTheme) => {
    setUiField("theme", nextTheme);
  }, [setUiField]);

  useEffect(() => {
    applyTheme(ui?.theme);
  }, [ui?.theme]);

  useEffect(() => {
    applyUiLanguage(ui?.uiLanguage);
  }, [ui?.uiLanguage]);

  useEffect(() => {
    if (typeof document === "undefined") return;
    const root = document.documentElement;
    root.setAttribute("data-motion", ui?.animationsEnabled === false ? "off" : "on");
  }, [ui?.animationsEnabled]);

  useEffect(() => {
    return () => {
      if (pulseTimerRef.current) clearTimeout(pulseTimerRef.current);
    };
  }, []);

  return {
    ui,
    setUi,
    setUiField,
    initialUi,
    sourceOptions,
    appOptions,
    categoryOptions,
    loading,
    loadError,
    isDirty,
    fieldErrors,
    saveError,
    pulseKinds,
    loadUiSettings,
    saveUiSettings,
    handleThemeChange,
  };
}
