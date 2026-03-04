import React, { useEffect, useMemo, useRef, useState } from "react";
import { useSubbarSetter } from "../../layout/useSubbar.js";
import SubAction from "../../ui/SubAction.jsx";
import Loader from "../../ui/Loader.jsx";
import Modal from "../../ui/Modal.jsx";
import SettingsUsers from "./SettingsUsers.jsx";
import useSecuritySettings from "./hooks/useSecuritySettings.js";
import { getSecurityText } from "./securityI18n.js";
import { sleep } from "./settingsUtils.js";

export default function SettingsSecurityPage() {
  const tSecurity = useMemo(() => getSecurityText(), []);
  const setContent = useSubbarSetter();
  const [saveState, setSaveState] = useState("idle");
  const [securityDowngradeModalOpen, setSecurityDowngradeModalOpen] = useState(false);
  const [authModesInfoOpen, setAuthModesInfoOpen] = useState(false);
  const hasLoadedRef = useRef(false);
  const handleRefreshRef = useRef(null);
  const handleSaveRef = useRef(null);
  const securitySettings = useSecuritySettings();

  const {
    security,
    setSecurity,
    loading,
    loadError,
    securityErrors,
    securityFieldErrors,
    securityMessage,
    passwordMessage,
    saveError,
    pulseKinds,
    requiresDowngradeConfirmation,
    credentialsRequiredForMode,
    effectiveAuthRequired,
    usernameRequired,
    passwordRequired,
    confirmRequired,
    canSave,
    isDirty,
    showExistingCredentialsHint,
    usernameFieldState,
    passwordFieldState,
    confirmFieldState,
    loadSecuritySettings,
    saveSecuritySettings,
  } = securitySettings;

  const handleRefresh = async () => {
    try {
      await loadSecuritySettings();
    } catch {
      // Load error is already exposed in hook state.
    }
  };

  const performSave = async (options = {}) => {
    if (!isDirty || saveState === "loading") return;

    const startedAt = Date.now();
    let ok = false;
    setSaveState("loading");

    try {
      await saveSecuritySettings(options);
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
  };

  const handleSave = async () => {
    if (!canSave) return;
    if (requiresDowngradeConfirmation) {
      setSecurityDowngradeModalOpen(true);
      return;
    }
    await performSave();
  };

  const closeSecurityDowngradeModal = () => {
    setSecurityDowngradeModalOpen(false);
  };

  const confirmSecurityDowngradeSave = async () => {
    setSecurityDowngradeModalOpen(false);
    await performSave({ allowDowngradeToOpen: true });
  };

  handleRefreshRef.current = handleRefresh;
  handleSaveRef.current = handleSave;

  useEffect(() => {
    if (hasLoadedRef.current) return;
    hasLoadedRef.current = true;
    void handleRefresh();
  }, []);

  useEffect(() => {
    setContent(
      <div className="settings-subbar-content">
        <SubAction icon="refresh" label="Rafraîchir" onClick={() => handleRefreshRef.current?.()} />
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
          disabled={loading || saveState === "loading" || !canSave}
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
          icon="info"
          label={tSecurity("settings.security.subbar.info")}
          onClick={() => setAuthModesInfoOpen(true)}
        />
      </div>
    );
    return () => setContent(null);
  }, [canSave, loading, saveState, setContent, tSecurity]);

  const pageError = saveError && !securityMessage ? saveError : "";

  return (
    <div className="page page--settings">
      <div className="pagehead">
        <div>
          <h1>Sécurité</h1>
          <div className="muted">Configuration de l&apos;application</div>
        </div>
      </div>
      <div className="pagehead__divider" />

      {loading && <Loader label="Chargement des paramètres sécurité…" />}

      {!loading && (loadError || pageError) && (
        <div className="errorbox">
          <div className="errorbox__title">Erreur</div>
          <div className="muted">{loadError || pageError}</div>
        </div>
      )}

      {!loading && !loadError && (
        <div className="settings-grid">
          <SettingsUsers
            security={security}
            setSecurity={setSecurity}
            securityErrors={securityErrors}
            securityFieldErrors={securityFieldErrors}
            securityMessage={securityMessage}
            passwordMessage={passwordMessage}
            securityPulseKinds={pulseKinds}
            showExistingCredentialsHint={showExistingCredentialsHint}
            credentialsRequiredForMode={credentialsRequiredForMode}
            effectiveAuthRequired={effectiveAuthRequired}
            usernameRequired={usernameRequired}
            passwordRequired={passwordRequired}
            confirmRequired={confirmRequired}
            usernameFieldState={usernameFieldState}
            passwordFieldState={passwordFieldState}
            confirmFieldState={confirmFieldState}
          />
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
