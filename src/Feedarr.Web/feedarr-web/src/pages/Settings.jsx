import React, { useEffect, useRef, useState } from "react";
import { useLocation } from "react-router-dom";
import { useSubbarSetter } from "../layout/useSubbar.js";
import SubAction from "../ui/SubAction.jsx";
import Loader from "../ui/Loader.jsx";

import useSettingsController from "./settings/useSettingsController.js";
import SettingsGeneral from "./settings/SettingsGeneral.jsx";
import SettingsProviders from "./settings/SettingsProviders.jsx";
import SettingsMaintenance from "./settings/SettingsMaintenance.jsx";
import SettingsBackup from "./settings/SettingsBackup.jsx";
import SettingsUiPage from "./settings/SettingsUiPage.jsx";
import SettingsSecurityPage from "./settings/SettingsSecurityPage.jsx";
import SettingsArrPage from "./settings/SettingsArrPage.jsx";

export default function Settings() {
  const location = useLocation();
  const section = location.pathname.split("/")[2] || "general";

  if (section === "ui") {
    return <SettingsUiPage />;
  }
  if (section === "users") {
    return <SettingsSecurityPage />;
  }
  if (section === "applications") {
    return <SettingsArrPage />;
  }

  return <SettingsStandardPage section={section} />;
}

function SettingsStandardPage({ section }) {
  const setContent = useSubbarSetter();
  const controller = useSettingsController(section);

  const {
    settingsTitle,
    loading,
    err,
    showGeneral,
    showExternals,
    showMaintenance,
    showBackup,
    handleRefresh,
    handleSave,
    saveState,
    canSave,
    openExternalModalAdd,
    canAddExternalProvider,
    general,
    providers,
    maintenance,
    backup,
  } = controller;

  // Refs for stable callback references in subbar - direct assignment (refs don't trigger re-renders)
  const openBackupCreateRef = useRef(null);
  const handleSaveRef = useRef(null);
  const openExternalModalAddRef = useRef(null);

  // Sync refs directly during render (safe because refs don't trigger re-renders)
  openBackupCreateRef.current = backup?.openBackupCreate;
  handleSaveRef.current = handleSave;
  openExternalModalAddRef.current = openExternalModalAdd;
  const [posterModalOpen, setPosterModalOpen] = useState(false);
  const backupActionsLocked = !!backup?.backupState?.isBusy || !!backup?.backupState?.needsRestart;
  const backupLockedTitle = backup?.backupState?.needsRestart
    ? "Redemarrage requis apres restauration"
    : "Operation de sauvegarde en cours";

  useEffect(() => {
    setContent(
      <div
        className="settings-subbar-content"
        subbarClassName=""
      >
        <SubAction icon="refresh" label="Rafraîchir" onClick={handleRefresh} />
        {showMaintenance && (
          <>
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
              label="Enregistrer"
              onClick={() => handleSaveRef.current?.()}
              disabled={saveState === "loading" || !canSave}
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
            <div className="subspacer" />
            <SubAction
              icon="settings"
              label="Options avancées"
              onClick={maintenance.toggleAdvancedOptions}
              active={!!maintenance.maintenanceSettings?.maintenanceAdvancedOptionsEnabled}
              className="subaction--maintenance-advanced"
              title={
                maintenance.maintenanceSettings?.maintenanceAdvancedOptionsEnabled
                  ? "Options avancées activées"
                  : "Afficher les options avancées"
              }
            />
          </>
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
        {!showMaintenance && !showBackup && !showExternals && (
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
            label="Enregistrer"
            onClick={() => handleSaveRef.current?.()}
            disabled={saveState === "loading" || !canSave}
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
        {showExternals && (
          <>
            <SubAction
              icon="add_circle"
              label="Ajouter"
              onClick={() => openExternalModalAddRef.current?.()}
              disabled={!canAddExternalProvider}
              title={!canAddExternalProvider ? "Aucun provider disponible" : "Ajouter"}
            />
            <SubAction icon="settings" label="Options" onClick={() => setPosterModalOpen(true)} />
          </>
        )}
      </div>
    );
    return () => setContent(null);
  }, [
    setContent,
    handleRefresh,
    showMaintenance,
    showBackup,
    showExternals,
    saveState,
    canSave,
    canAddExternalProvider,
    backupActionsLocked,
    backupLockedTitle,
    maintenance.toggleAdvancedOptions,
    maintenance.maintenanceSettings?.maintenanceAdvancedOptionsEnabled,
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
          {showExternals && <SettingsProviders controller={providers} posterModalOpen={posterModalOpen} closePosterModal={() => setPosterModalOpen(false)} />}
          {showMaintenance && <SettingsMaintenance {...maintenance} />}
          {showBackup && <SettingsBackup {...backup} />}
        </div>
      )}
    </div>
  );
}
