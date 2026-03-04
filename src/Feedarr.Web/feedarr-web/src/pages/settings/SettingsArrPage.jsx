import React, { useCallback, useEffect, useRef, useState } from "react";
import { useSubbarSetter } from "../../layout/useSubbar.js";
import SubAction from "../../ui/SubAction.jsx";
import Loader from "../../ui/Loader.jsx";
import SettingsApplications from "./SettingsApplications.jsx";
import useArrApplications from "./hooks/useArrApplications.js";

export default function SettingsArrPage() {
  const setContent = useSubbarSetter();
  const applications = useArrApplications();
  const {
    arrSettingsLoadError,
    arrSettingsLoading,
    arrSyncing,
    availableAddTypes,
    hasEnabledArrApps,
    loadArrApps,
    loadArrSyncSettings,
    loadArrSyncStatus,
    openArrModalAdd,
    triggerArrSync,
  } = applications;
  const [pageLoading, setPageLoading] = useState(true);
  const [pageError, setPageError] = useState("");
  const [optionsModalOpen, setOptionsModalOpen] = useState(false);
  const hasLoadedRef = useRef(false);
  const handleRefreshRef = useRef(null);

  const handleRefresh = useCallback(async () => {
    setPageLoading(true);
    setPageError("");

    try {
      await Promise.all([
        loadArrApps(),
        loadArrSyncSettings(),
        loadArrSyncStatus(),
      ]);
    } catch (error) {
      setPageError(error?.message || "Erreur chargement paramètres applications");
    } finally {
      setPageLoading(false);
    }
  }, [loadArrApps, loadArrSyncSettings, loadArrSyncStatus]);

  const handleSyncAll = useCallback(async () => {
    try {
      await triggerArrSync();
    } catch (error) {
      setPageError(error?.message || "Erreur synchronisation applications");
    }
  }, [triggerArrSync]);

  handleRefreshRef.current = handleRefresh;

  useEffect(() => {
    if (hasLoadedRef.current) return;
    hasLoadedRef.current = true;
    void handleRefresh();
  }, [handleRefresh]);

  useEffect(() => {
    setContent(
      <div className="settings-subbar-content subbar--settings-apps-sync">
        <SubAction icon="refresh" label="Rafraîchir" onClick={() => handleRefreshRef.current?.()} />
        <SubAction
          icon="add_circle"
          label="Ajouter"
          onClick={openArrModalAdd}
          disabled={availableAddTypes.length === 0}
          title={availableAddTypes.length === 0 ? "Toutes les applications sont déjà ajoutées" : "Ajouter"}
        />
        <SubAction icon="settings" label="Options" onClick={() => setOptionsModalOpen(true)} />
        <div className="subspacer" />
        <SubAction
          icon={arrSyncing ? "progress_activity" : "sync"}
          label="Sync all"
          onClick={handleSyncAll}
          disabled={!hasEnabledArrApps || arrSyncing}
          title={!hasEnabledArrApps ? "Aucune application active" : "Sync all"}
          className={arrSyncing ? "is-loading" : undefined}
        />
      </div>
    );
    return () => setContent(null);
  }, [
    arrSyncing,
    availableAddTypes.length,
    hasEnabledArrApps,
    openArrModalAdd,
    handleSyncAll,
    setContent,
  ]);

  const effectiveLoadError = pageError || arrSettingsLoadError;
  const loading = pageLoading || arrSettingsLoading;

  return (
    <div className="page page--settings">
      <div className="pagehead">
        <div>
          <h1>Applications</h1>
          <div className="muted">Configuration de l&apos;application</div>
        </div>
      </div>
      <div className="pagehead__divider" />

      {loading && <Loader label="Chargement des paramètres applications…" />}

      {!loading && effectiveLoadError && (
        <div className="errorbox">
          <div className="errorbox__title">Erreur</div>
          <div className="muted">{effectiveLoadError}</div>
        </div>
      )}

      {!loading && !effectiveLoadError && (
        <div className="settings-grid">
          <SettingsApplications
            {...applications}
            optionsModalOpen={optionsModalOpen}
            closeOptionsModal={() => setOptionsModalOpen(false)}
          />
        </div>
      )}
    </div>
  );
}
