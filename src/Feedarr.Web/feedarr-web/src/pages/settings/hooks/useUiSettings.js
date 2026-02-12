import { useCallback, useEffect, useRef, useState } from "react";
import { apiGet, apiPut } from "../../../api/client.js";
import { applyTheme, getStoredTheme } from "../../../app/theme.js";

const defaultUi = {
  hideSeenByDefault: false,
  showCategories: true,
  defaultView: "grid",
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
    hideSeenByDefault: !!merged.hideSeenByDefault,
    showCategories: !!merged.showCategories,
    defaultView: merged.defaultView || "grid",
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
  const [ui, setUi] = useState({ ...defaultUi, theme: storedTheme });
  const [initialUi, setInitialUi] = useState({ ...defaultUi, theme: storedTheme });
  const [pulseKeys, setPulseKeys] = useState(() => new Set());
  const pulseTimerRef = useRef(null);

  const isDirty = JSON.stringify(ui) !== JSON.stringify(initialUi);

  const loadUiSettings = useCallback(async () => {
    try {
      const u = await apiGet("/api/settings/ui");
      if (u) {
        const normalizedUi = {
          ...u,
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
    if (ui.hideSeenByDefault !== initialUi.hideSeenByDefault) changed.add("ui.hideSeen");
    if (ui.showCategories !== initialUi.showCategories) changed.add("ui.showCategories");
    if (ui.defaultView !== initialUi.defaultView) changed.add("ui.defaultView");
    if (ui.theme !== initialUi.theme) changed.add("ui.theme");
    if (ui.enableMissingPosterView !== initialUi.enableMissingPosterView) changed.add("ui.missingPosterView");
    if (ui.animationsEnabled !== initialUi.animationsEnabled) changed.add("ui.animations");

    await apiPut("/api/settings/ui", buildUiPayload(ui));
    setInitialUi(buildUiPayload(ui));
    applyTheme(ui.theme, true);

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
