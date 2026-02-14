import React, { useEffect, useRef } from "react";
import { useLocation } from "react-router-dom";
import { useSubbarSetter } from "../layout/useSubbar.js";
import SubAction from "../ui/SubAction.jsx";
import Loader from "../ui/Loader.jsx";

import useSettingsController from "./settings/useSettingsController.js";
import SettingsGeneral from "./settings/SettingsGeneral.jsx";
import SettingsUI from "./settings/SettingsUI.jsx";
import SettingsProviders from "./settings/SettingsProviders.jsx";
import SettingsApplications from "./settings/SettingsApplications.jsx";
import SettingsMaintenance from "./settings/SettingsMaintenance.jsx";
import SettingsBackup from "./settings/SettingsBackup.jsx";
import SettingsUsers from "./settings/SettingsUsers.jsx";

export default function Settings() {
  const setContent = useSubbarSetter();
  const location = useLocation();
  const section = location.pathname.split("/")[2] || "general";
  const controller = useSettingsController(section);

  const {
    settingsTitle,
    loading,
    err,
    showGeneral,
    showUi,
    showExternals,
    showApplications,
    showMaintenance,
    showBackup,
    showUsers,
    handleRefresh,
    handleSave,
    saveState,
    isDirty,
    openArrModalAdd,
    canAddArrApp,
    triggerArrSync,
    arrSyncing,
    hasEnabledArrApps,
    general,
    ui,
    providers,
    applications,
    maintenance,
    backup,
    users,
  } = controller;

  // Refs for stable callback references in subbar - direct assignment (refs don't trigger re-renders)
  const openBackupCreateRef = useRef(null);
  const handleRetroFetchRef = useRef(null);
  const handleRetroFetchStopRef = useRef(null);
  const handleSaveRef = useRef(null);
  const openArrModalAddRef = useRef(null);

  // Sync refs directly during render (safe because refs don't trigger re-renders)
  openBackupCreateRef.current = backup?.openBackupCreate;
  handleRetroFetchRef.current = providers?.handleRetroFetch;
  handleRetroFetchStopRef.current = providers?.handleRetroFetchStop;
  handleSaveRef.current = handleSave;
  openArrModalAddRef.current = openArrModalAdd;
  const backupActionsLocked = !!backup?.backupState?.isBusy || !!backup?.backupState?.needsRestart;
  const backupLockedTitle = backup?.backupState?.needsRestart
    ? "Redemarrage requis apres restauration"
    : "Operation de sauvegarde en cours";

  useEffect(() => {
    setContent(
      <>
        <SubAction icon="refresh" label="Rafraîchir" onClick={handleRefresh} />
        {showApplications && (
          <SubAction
            icon="add_circle"
            label="Ajouter"
            onClick={() => openArrModalAddRef.current?.()}
            disabled={!canAddArrApp}
            title={!canAddArrApp ? "Toutes les applications sont déjà ajoutées" : "Ajouter"}
          />
        )}
        {showBackup && (
          <SubAction
            icon="add_circle"
            label="Ajouter"
            onClick={() => openBackupCreateRef.current?.()}
            disabled={backupActionsLocked}
            title={backupActionsLocked ? backupLockedTitle : "Ajouter"}
          />
        )}
        {!showMaintenance && !showBackup && (
          <SubAction
            icon={
              saveState === "loading"
                ? "progress_activity"
                : saveState === "success"
                ? "check_circle"
                : saveState === "error"
                ? "cancel"
                : "save"
            }
            label="Sauver"
            onClick={() => handleSaveRef.current?.()}
            disabled={saveState === "loading" || !isDirty}
            className={
              saveState === "loading"
                ? "is-loading"
                : saveState === "success"
                ? "is-success"
                : saveState === "error"
                ? "is-error"
                : ""
            }
          />
        )}
        {showApplications && (
          <>
            <div className="subspacer" />
            <SubAction
              icon={arrSyncing ? "progress_activity" : "sync"}
              label="Sync all"
              onClick={triggerArrSync}
              disabled={!hasEnabledArrApps || arrSyncing}
              title={!hasEnabledArrApps ? "Aucune application active" : "Sync all"}
              className={arrSyncing ? "is-loading" : undefined}
            />
          </>
        )}
        {showExternals && (
          <>
            <div className="subspacer" />
            {providers?.retroActive ? (
              <SubAction
                icon="progress_activity"
                label={`${providers?.retroPercent ?? 0}%`}
                onClick={() => handleRetroFetchStopRef.current?.()}
                disabled={providers?.retroStopLoading}
                title="Arrêter le retro fetch"
                className="is-loading"
              />
            ) : (
              <SubAction
                icon={providers?.retroLoading ? "progress_activity" : "image_search"}
                label="Retro Fetch"
                onClick={() => handleRetroFetchRef.current?.()}
                disabled={providers?.retroLoading}
                title="Rechercher les posters manquants"
                className={providers?.retroLoading ? "is-loading" : undefined}
              />
            )}
          </>
        )}
      </>
    );
    return () => setContent(null);
  }, [
    setContent,
    handleRefresh,
    showApplications,
    showMaintenance,
    showBackup,
    showExternals,
    saveState,
    isDirty,
    canAddArrApp,
    triggerArrSync,
    arrSyncing,
    hasEnabledArrApps,
    providers?.retroActive,
    providers?.retroLoading,
    providers?.retroStopLoading,
    providers?.retroPercent,
    backupActionsLocked,
    backupLockedTitle,
  ]);

  return (
    <div className="page page--settings">
      <div className="pagehead">
        <div>
          <h1>{settingsTitle}</h1>
          <div className="muted">Configuration de l'application</div>
        </div>
      </div>
      <div className="pagehead__divider" />

      {loading && <Loader label="Chargement des paramètres…" />}

      {!loading && err && (
        <div className="errorbox">
          <div className="errorbox__title">Erreur</div>
          <div className="muted">{err}</div>
        </div>
      )}

      {!loading && !err && (
        <div className="settings-grid">
          {showGeneral && <SettingsGeneral {...general} />}
          {showUi && <SettingsUI {...ui} />}
          {showExternals && <SettingsProviders {...providers} />}
          {showApplications && <SettingsApplications {...applications} />}
          {showMaintenance && <SettingsMaintenance {...maintenance} />}
          {showBackup && <SettingsBackup {...backup} />}
          {showUsers && <SettingsUsers {...users} />}
        </div>
      )}
    </div>
  );
}
