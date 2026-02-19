import { useCallback, useEffect, useRef, useState } from "react";
import { apiGet, apiPut } from "../../../api/client.js";
import { applyTheme, getStoredTheme } from "../../../app/theme.js";
import {
  applyUiLanguage,
  DEFAULT_UI_LANGUAGE,
  DEFAULT_MEDIA_INFO_LANGUAGE,
  getStoredUiLanguage,
  normalizeMediaInfoLanguage,
  normalizeUiLanguage,
} from "../../../app/locale.js";

const defaultUi = {
  uiLanguage: DEFAULT_UI_LANGUAGE,
  mediaInfoLanguage: DEFAULT_MEDIA_INFO_LANGUAGE,
  hideSeenByDefault: false,
  showCategories: true,
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

export function buildUiPayload(source, overrides = {}) {
  const merged = { ...source, ...overrides };
  return {
    uiLanguage: normalizeUiLanguage(merged.uiLanguage),
    mediaInfoLanguage: normalizeMediaInfoLanguage(merged.mediaInfoLanguage),
    hideSeenByDefault: !!merged.hideSeenByDefault,
    showCategories: !!merged.showCategories,
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

export default function useUiSettings() {
  const storedTheme = getStoredTheme();
  const storedUiLanguage = getStoredUiLanguage();
  const [ui, setUi] = useState({ ...defaultUi, theme: storedTheme, uiLanguage: storedUiLanguage });
  const [initialUi, setInitialUi] = useState({ ...defaultUi, theme: storedTheme, uiLanguage: storedUiLanguage });
  const [pulseKeys, setPulseKeys] = useState(() => new Set());
  const pulseTimerRef = useRef(null);

  const isDirty = JSON.stringify(ui) !== JSON.stringify(initialUi);

  const loadUiSettings = useCallback(async () => {
    try {
      const u = await apiGet("/api/settings/ui");
      if (u) {
        const normalizedUi = {
          ...u,
          uiLanguage: normalizeUiLanguage(u.uiLanguage),
          mediaInfoLanguage: normalizeMediaInfoLanguage(u.mediaInfoLanguage),
          defaultSort: u.defaultSort || "date",
          defaultMaxAgeDays: u.defaultMaxAgeDays ?? "",
          defaultLimit: u.defaultLimit ?? 100,
          defaultFilterSeen: u.defaultFilterSeen ?? "",
          defaultFilterApplication: u.defaultFilterApplication ?? "",
          defaultFilterSourceId: u.defaultFilterSourceId ?? "",
          defaultFilterCategoryId: u.defaultFilterCategoryId ?? "",
          defaultFilterQuality: u.defaultFilterQuality ?? "",
          theme: u.theme || "light",
          enableMissingPosterView: !!u.enableMissingPosterView,
          animationsEnabled: u.animationsEnabled !== false,
          onboardingDone: !!u.onboardingDone,
        };
        setUi(normalizedUi);
        setInitialUi(normalizedUi);
      }
    } catch {
      // Ignore load errors
    }
  }, []);

  const saveUiSettings = useCallback(async () => {
    const changed = new Set();
    if (ui.uiLanguage !== initialUi.uiLanguage) changed.add("ui.uiLanguage");
    if (ui.mediaInfoLanguage !== initialUi.mediaInfoLanguage) changed.add("ui.mediaInfoLanguage");
    if (ui.hideSeenByDefault !== initialUi.hideSeenByDefault) changed.add("ui.hideSeen");
    if (ui.showCategories !== initialUi.showCategories) changed.add("ui.showCategories");
    if (ui.defaultView !== initialUi.defaultView) changed.add("ui.defaultView");
    if (ui.defaultSort !== initialUi.defaultSort) changed.add("ui.defaultSort");
    if (ui.defaultMaxAgeDays !== initialUi.defaultMaxAgeDays) changed.add("ui.defaultMaxAgeDays");
    if (ui.defaultLimit !== initialUi.defaultLimit) changed.add("ui.defaultLimit");
    if (ui.defaultFilterSeen !== initialUi.defaultFilterSeen) changed.add("ui.defaultFilterSeen");
    if (ui.defaultFilterApplication !== initialUi.defaultFilterApplication) changed.add("ui.defaultFilterApplication");
    if (ui.defaultFilterSourceId !== initialUi.defaultFilterSourceId) changed.add("ui.defaultFilterSourceId");
    if (ui.defaultFilterCategoryId !== initialUi.defaultFilterCategoryId) changed.add("ui.defaultFilterCategoryId");
    if (ui.defaultFilterQuality !== initialUi.defaultFilterQuality) changed.add("ui.defaultFilterQuality");
    if (ui.theme !== initialUi.theme) changed.add("ui.theme");
    if (ui.enableMissingPosterView !== initialUi.enableMissingPosterView) changed.add("ui.missingPosterView");
    if (ui.animationsEnabled !== initialUi.animationsEnabled) changed.add("ui.animations");

    await apiPut("/api/settings/ui", buildUiPayload(ui));
    setInitialUi(buildUiPayload(ui));
    applyTheme(ui.theme, true);
    applyUiLanguage(ui.uiLanguage, true);

    // Pulse effect for changed fields
    if (changed.size > 0) {
      if (pulseTimerRef.current) clearTimeout(pulseTimerRef.current);
      setPulseKeys(new Set(changed));
      pulseTimerRef.current = setTimeout(() => {
        setPulseKeys(new Set());
      }, 1200);
    }

    return changed;
  }, [ui, initialUi]);

  const handleThemeChange = useCallback((nextTheme) => {
    setUi((u) => ({ ...u, theme: nextTheme }));
  }, []);

  // Apply theme changes in real-time (preview)
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

  return {
    ui,
    setUi,
    initialUi,
    isDirty,
    pulseKeys,
    loadUiSettings,
    saveUiSettings,
    handleThemeChange,
  };
}
