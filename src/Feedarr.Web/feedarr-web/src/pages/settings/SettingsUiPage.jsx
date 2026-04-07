
import React, { useCallback, useEffect, useRef, useState } from "react";
import { useSubbarSetter } from "../../layout/useSubbar.js";
import SubAction from "../../ui/SubAction.jsx";
import Loader from "../../ui/Loader.jsx";
import SettingsUI from "./SettingsUI.jsx";
import useUiSettings from "./hooks/useUiSettings.js";
import { sleep } from "./settingsUtils.js";
import { getSaveActionVisualState } from "./settingsUiPageModel.js";

export default function SettingsUiPage() {
  const setContent = useSubbarSetter();
  const [saveState, setSaveState] = useState("idle");
  const uiSettings = useUiSettings();
  const hasLoadedRef = useRef(false);
  const handleSaveRef = useRef(null);
  const handleRefreshRef = useRef(null);

  const {
    ui,
    setUiField,
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
  } = uiSettings;

  const handleRefresh = useCallback(async () => {
    try {
      await loadUiSettings();
    } catch {
      // Inline error state is already managed by the hook.
    }
  }, [loadUiSettings]);

  const handleSave = useCallback(async () => {
    if (!isDirty || saveState === "loading") return;

    const startedAt = Date.now();
    let ok = false;
    setSaveState("loading");

    try {
      await saveUiSettings();
      ok = true;
    } catch {
      ok = false;
    } finally {
      const elapsed = Date.now() - startedAt;
      if (elapsed < 1000) {
        await sleep(1000 - elapsed);
      }
      setSaveState(ok ? "success" : "error");
      setTimeout(() => setSaveState("idle"), 1000);
    }
  }, [isDirty, saveState, saveUiSettings]);

  handleSaveRef.current = handleSave;
  handleRefreshRef.current = handleRefresh;

  useEffect(() => {
    if (hasLoadedRef.current) return;
    hasLoadedRef.current = true;
    void handleRefresh();
  }, [handleRefresh]);

  useEffect(() => {
    const saveActionVisualState = getSaveActionVisualState(saveState);
    setContent(
      <div className="settings-subbar-content">
        <SubAction icon="refresh" label="Rafraîchir" onClick={() => handleRefreshRef.current?.()} />
        <SubAction
          icon={saveActionVisualState.icon}
          label="Enregistrer"
          onClick={() => handleSaveRef.current?.()}
          disabled={loading || saveState === "loading" || !isDirty}
          className={saveActionVisualState.className}
        />
      </div>
    );
    return () => setContent(null);
  }, [isDirty, loading, saveState, setContent]);

  return (
    <div className="page page--settings">
      <div className="pagehead">
        <div>
          <h1>UI</h1>
          <div className="muted">Configuration de l&apos;application</div>
        </div>
      </div>
      <div className="pagehead__divider" />

      {loading && <Loader label="Chargement des paramètres UI…" />}

      {!loading && loadError && (
        <div className="errorbox">
          <div className="errorbox__title">Erreur</div>
          <div className="muted">{loadError}</div>
        </div>
      )}

      {!loading && !loadError && (
        <div className="settings-grid">
          <SettingsUI
            ui={ui}
            setUiField={setUiField}
            pulseKinds={pulseKinds}
            fieldErrors={fieldErrors}
            saveError={saveError}
            handleThemeChange={handleThemeChange}
            sourceOptions={sourceOptions}
            appOptions={appOptions}
            categoryOptions={categoryOptions}
          />
        </div>
      )}
    </div>
  );
}
