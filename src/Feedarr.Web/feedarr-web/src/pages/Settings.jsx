import React, { useEffect, useMemo, useRef, useState } from "react";
import { useLocation } from "react-router-dom";
import { useSubbarSetter } from "../layout/useSubbar.js";
import SubAction from "../ui/SubAction.jsx";
import Loader from "../ui/Loader.jsx";
import Modal from "../ui/Modal.jsx";

import useSettingsController from "./settings/useSettingsController.js";
import SettingsGeneral from "./settings/SettingsGeneral.jsx";
import SettingsUI from "./settings/SettingsUI.jsx";
import SettingsProviders from "./settings/SettingsProviders.jsx";
import SettingsApplications from "./settings/SettingsApplications.jsx";
import SettingsMaintenance from "./settings/SettingsMaintenance.jsx";
import SettingsBackup from "./settings/SettingsBackup.jsx";
import SettingsUsers from "./settings/SettingsUsers.jsx";
import { getSecurityText } from "./settings/securityI18n.js";

export default function Settings() {
  const tSecurity = useMemo(() => getSecurityText(), []);
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
    securityDowngradeModalOpen,
    closeSecurityDowngradeModal,
    confirmSecurityDowngradeSave,
    isDirty,
    isSaveBlocked,
    openArrModalAdd,
    canAddArrApp,
    openExternalModalAdd,
    canAddExternalProvider,
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
  const handleSaveRef = useRef(null);
  const openArrModalAddRef = useRef(null);
  const openExternalModalAddRef = useRef(null);

  // Sync refs directly during render (safe because refs don't trigger re-renders)
  openBackupCreateRef.current = backup?.openBackupCreate;
  handleSaveRef.current = handleSave;
  openArrModalAddRef.current = openArrModalAdd;
  openExternalModalAddRef.current = openExternalModalAdd;
  const [posterModalOpen, setPosterModalOpen] = useState(false);
  const [applicationsOptionsOpen, setApplicationsOptionsOpen] = useState(false);
  const [authModesInfoOpen, setAuthModesInfoOpen] = useState(false);
  const backupActionsLocked = !!backup?.backupState?.isBusy || !!backup?.backupState?.needsRestart;
  const backupLockedTitle = backup?.backupState?.needsRestart
    ? "Redemarrage requis apres restauration"
    : "Operation de sauvegarde en cours";

  useEffect(() => {
    if (!showApplications) {
      setApplicationsOptionsOpen(false);
    }
  }, [showApplications]);

  useEffect(() => {
    setContent(
      <div
        className="settings-subbar-content"
        subbarClassName={showApplications ? "subbar--settings-apps-sync" : ""}
      >
        <SubAction icon="refresh" label="Rafraîchir" onClick={handleRefresh} />
        {showApplications && (
          <>
            <SubAction
              icon="add_circle"
              label="Ajouter"
              onClick={() => openArrModalAddRef.current?.()}
              disabled={!canAddArrApp}
              title={!canAddArrApp ? "Toutes les applications sont déjà ajoutées" : "Ajouter"}
            />
            <SubAction icon="settings" label="Options" onClick={() => setApplicationsOptionsOpen(true)} />
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
        {!showMaintenance && !showBackup && !showExternals && !showApplications && (
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
            disabled={saveState === "loading" || !isDirty || isSaveBlocked}
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
        {showUsers && (
          <>
            <div className="subspacer" />
            <SubAction
              icon="info"
              label={tSecurity("settings.security.subbar.info")}
              onClick={() => setAuthModesInfoOpen(true)}
            />
          </>
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
    showApplications,
    showMaintenance,
    showBackup,
    showExternals,
    saveState,
    isDirty,
    isSaveBlocked,
    canAddArrApp,
    triggerArrSync,
    arrSyncing,
    hasEnabledArrApps,
    canAddExternalProvider,
    backupActionsLocked,
    backupLockedTitle,
    tSecurity,
    showUsers,
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
          {showExternals && <SettingsProviders controller={providers} posterModalOpen={posterModalOpen} closePosterModal={() => setPosterModalOpen(false)} />}
          {showApplications && (
            <SettingsApplications
              {...applications}
              optionsModalOpen={applicationsOptionsOpen}
              closeOptionsModal={() => setApplicationsOptionsOpen(false)}
            />
          )}
          {showMaintenance && <SettingsMaintenance {...maintenance} />}
          {showBackup && <SettingsBackup {...backup} />}
          {showUsers && <SettingsUsers {...users} />}
        </div>
      )}

      <Modal
        open={securityDowngradeModalOpen}
        title={tSecurity("settings.security.modal.disableAuth.title")}
        onClose={closeSecurityDowngradeModal}
        width={560}
      >
        <div style={{ padding: 12 }}>
          <div className="muted" style={{ marginBottom: 12 }}>
            {tSecurity("settings.security.modal.disableAuth.message")}
          </div>
          <div className="formactions">
            <button className="btn" type="button" onClick={closeSecurityDowngradeModal} disabled={saveState === "loading"}>
              {tSecurity("settings.security.modal.cancel")}
            </button>
            <button className="btn btn-danger" type="button" onClick={confirmSecurityDowngradeSave} disabled={saveState === "loading"}>
              {tSecurity("settings.security.modal.confirm")}
            </button>
          </div>
        </div>
      </Modal>

      <Modal
        open={authModesInfoOpen}
        title={tSecurity("settings.security.infoModal.title")}
        onClose={() => setAuthModesInfoOpen(false)}
        width={640}
      >
        <div style={{ padding: 12, display: "grid", gap: 12 }}>
          <div>
            <div style={{ fontWeight: 700, marginBottom: 4 }}>{tSecurity("settings.security.infoModal.none.title")}</div>
            <div className="muted">{tSecurity("settings.security.infoModal.none.description")}</div>
          </div>
          <div>
            <div style={{ fontWeight: 700, marginBottom: 4 }}>{tSecurity("settings.security.infoModal.smart.title")}</div>
            <div className="muted">{tSecurity("settings.security.infoModal.smart.description")}</div>
          </div>
          <div>
            <div style={{ fontWeight: 700, marginBottom: 4 }}>{tSecurity("settings.security.infoModal.strict.title")}</div>
            <div className="muted">{tSecurity("settings.security.infoModal.strict.description")}</div>
          </div>
          <div className="muted" style={{ fontStyle: "italic" }}>
            {tSecurity("settings.security.infoModal.note")}
          </div>
          <div className="formactions">
            <button className="btn" type="button" onClick={() => setAuthModesInfoOpen(false)}>
              {tSecurity("settings.security.infoModal.close")}
            </button>
          </div>
        </div>
      </Modal>
    </div>
  );
}
