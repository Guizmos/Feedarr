import React from "react";
import Modal from "../../ui/Modal.jsx";
import ExternalProviderInstancesSection from "./ExternalProviderInstancesSection.jsx";

export default function SettingsProviders({
  controller,
  posterModalOpen,
  closePosterModal,
}) {
  const {
    posterCount,
    missingPosterCount,
    releasesCount,
    retroActive,
    retroLoading,
    retroStopLoading,
    retroPercent,
    retroDone,
    retroTotal,
    handleRetroFetch,
    handleRetroFetchStop,
  } = controller;

  return (
    <>
      <div id="externals">
        <ExternalProviderInstancesSection controller={controller} />
      </div>

      <Modal
        open={posterModalOpen}
        title="Posters"
        onClose={closePosterModal}
        width={520}
      >
        <div style={{ padding: 12 }}>
          <div className="indexer-list">
            <div className="indexer-card">
              <div className="indexer-row indexer-row--settings">
                <span className="indexer-title">Posters locaux</span>
                <div className="indexer-actions">
                  <span className="indexer-status">{posterCount}</span>
                </div>
              </div>
            </div>
            <div className="indexer-card">
              <div className="indexer-row indexer-row--settings">
                <span className="indexer-title">Posters manquants</span>
                <div className="indexer-actions">
                  <span className="indexer-status">{Math.max(0, missingPosterCount || 0)}</span>
                </div>
              </div>
            </div>
            <div className="indexer-card">
              <div className="indexer-row indexer-row--settings">
                <span className="indexer-title">Pourcentage de matching</span>
                <div className="indexer-actions">
                  <span className="indexer-status">
                    {releasesCount > 0
                      ? Math.round(((releasesCount - (missingPosterCount || 0)) / releasesCount) * 100)
                      : 0}%
                  </span>
                </div>
              </div>
            </div>
          </div>

          <div style={{ marginTop: 16, display: "flex", alignItems: "center", gap: 10 }}>
            <div
              className="muted"
              style={{
                fontSize: 11,
                lineHeight: 1.2,
                flex: 1,
                minWidth: 0,
                whiteSpace: "nowrap",
                overflow: "hidden",
                textOverflow: "ellipsis",
              }}
            >
              Recherche des posters manquants via les providers configurés.
            </div>
            {retroActive ? (
              <button
                className="btn btn-fixed-danger btn-nohover"
                type="button"
                onClick={handleRetroFetchStop}
                disabled={retroStopLoading}
              >
                {retroStopLoading ? "Arrêt..." : `Arrêter Retro Fetch (${retroPercent ?? 0}% — ${retroDone ?? 0}/${retroTotal ?? 0})`}
              </button>
            ) : (
              <button
                className="btn btn-hover-ok"
                type="button"
                onClick={handleRetroFetch}
                disabled={retroLoading}
              >
                {retroLoading ? "Lancement..." : "Retro Fetch"}
              </button>
            )}
          </div>
        </div>
      </Modal>
    </>
  );
}
