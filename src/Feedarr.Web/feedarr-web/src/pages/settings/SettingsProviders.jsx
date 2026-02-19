import React from "react";
import Modal from "../../ui/Modal.jsx";
import ItemRow from "../../ui/ItemRow.jsx";

export default function SettingsProviders({
  externalFlags,
  providerStats,
  testingExternal,
  testStatusByExternal,
  testExternal,
  openExternalModal,
  openExternalDisable,
  openExternalToggle,
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
  posterModalOpen,
  closePosterModal,
  externalModalOpen,
  externalModalRow,
  externalModalValue,
  setExternalModalValue,
  externalModalValue2,
  setExternalModalValue2,
  externalModalTesting,
  externalModalTested,
  externalModalError,
  setExternalModalTested,
  setExternalModalError,
  closeExternalModal,
  testExternalModal,
  saveExternalModal,
  externalDisableOpen,
  externalDisableRow,
  closeExternalDisable,
  confirmDisableExternal,
  externalToggleOpen,
  externalToggleRow,
  closeExternalToggle,
  confirmToggleExternal,
}) {
  return (
    <>
      <div id="externals">
        <div className="indexer-list itemrow-grid">
          {[
            {
              key: "tmdb",
              title: "TMDB",
              has: externalFlags.hasTmdbApiKey,
              enabled: externalFlags.tmdbEnabled,
              inputKey: "tmdbApiKey",
              kind: "tmdb",
              disableKeys: ["tmdbApiKey"],
              toggleKey: "tmdbEnabled",
              calls: providerStats?.tmdb?.calls ?? 0,
              failures: providerStats?.tmdb?.failures ?? 0,
            },
            {
              key: "tvmaze",
              title: "TVmaze",
              has: externalFlags.tvmazeEnabled || externalFlags.hasTvmazeApiKey,
              enabled: externalFlags.tvmazeEnabled,
              inputKey: "tvmazeApiKey",
              kind: "tvmaze",
              disableKeys: ["tvmazeApiKey"],
              toggleKey: "tvmazeEnabled",
              calls: providerStats?.tvmaze?.calls ?? 0,
              failures: providerStats?.tvmaze?.failures ?? 0,
            },
            {
              key: "fanart",
              title: "Fanart TV",
              has: externalFlags.hasFanartApiKey,
              enabled: externalFlags.fanartEnabled,
              inputKey: "fanartApiKey",
              kind: "fanart",
              disableKeys: ["fanartApiKey"],
              toggleKey: "fanartEnabled",
              calls: providerStats?.fanart?.calls ?? 0,
              failures: providerStats?.fanart?.failures ?? 0,
            },
            {
              key: "igdb",
              title: "IGDB",
              has: externalFlags.hasIgdbClientId && externalFlags.hasIgdbClientSecret,
              enabled: externalFlags.igdbEnabled,
              inputKey: "igdbClientId",
              inputKey2: "igdbClientSecret",
              kind: "igdb",
              disableKeys: ["igdbClientId", "igdbClientSecret"],
              toggleKey: "igdbEnabled",
              calls: providerStats?.igdb?.calls ?? 0,
              failures: providerStats?.igdb?.failures ?? 0,
            },
          ].map((row, idx) => {
            const statusOk = testStatusByExternal[row.key] === "ok";
            const statusErr = testStatusByExternal[row.key] === "error";
            const isTesting = testingExternal === row.key;

            const statusClass = [
              statusOk && "test-ok",
              statusErr && "test-err",
            ].filter(Boolean).join(" ");

            return (
              <ItemRow
                key={row.key}
                id={idx + 1}
                title={row.title}
                meta={`Appels: ${row.calls} | Echecs: ${row.failures}`}
                enabled={row.enabled}
                statusClass={statusClass}
                badges={[
                  {
                    label: row.has ? "OK" : "NO",
                    className: row.has ? "pill-ok" : "pill-warn",
                  },
                ]}
                actions={[
                  {
                    icon: "science",
                    title: isTesting
                      ? "Test en cours..."
                      : !row.enabled
                      ? "Activez d'abord le provider"
                      : "Tester",
                    onClick: () => testExternal(row.key, row.kind),
                    disabled: isTesting || !row.enabled,
                    spinning: isTesting,
                  },
                  {
                    icon: "edit",
                    title: !row.enabled ? "Activez d'abord le provider" : "Modifier",
                    onClick: () => openExternalModal(row),
                    disabled: isTesting || !row.enabled,
                  },
                  {
                    icon: "delete",
                    title: "Desactiver et supprimer la cle",
                    onClick: () => openExternalDisable(row),
                    disabled: isTesting,
                    className: "iconbtn--danger",
                  },
                ]}
                showToggle
                onToggle={() => openExternalToggle(row)}
                toggleDisabled={isTesting}
              />
            );
          })}
        </div>
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

      <Modal
        open={externalModalOpen}
        title={externalModalRow ? `Modifier : ${externalModalRow.title}` : "Modifier"}
        onClose={closeExternalModal}
        width={520}
      >
        <div style={{ padding: 12 }}>
          <div className="field" style={{ marginBottom: 10 }}>
            <label className="muted">
              {externalModalRow?.inputKey2 ? "Client ID" : "Clé API"}
            </label>
            <input
              type="password"
              value={externalModalValue}
              onChange={(e) => {
                setExternalModalValue(e.target.value);
                setExternalModalTested(false);
                setExternalModalError("");
              }}
              placeholder={externalModalRow?.inputKey2 ? "Entrez le Client ID" : "Entrez la clé API"}
              disabled={externalModalTesting}
            />
          </div>

          {externalModalRow?.inputKey2 && (
            <div className="field" style={{ marginBottom: 10 }}>
              <label className="muted">Client Secret</label>
              <input
                type="password"
                value={externalModalValue2}
                onChange={(e) => {
                  setExternalModalValue2(e.target.value);
                  setExternalModalTested(false);
                  setExternalModalError("");
                }}
                placeholder="Entrez le Client Secret"
                disabled={externalModalTesting}
              />
            </div>
          )}

          {externalModalError && (
            <div className="onboarding__error" style={{ marginBottom: 12 }}>
              {externalModalError}
            </div>
          )}

          {externalModalTested && (
            <div className="onboarding__ok" style={{ marginBottom: 12 }}>
              Connexion réussie ! Vous pouvez enregistrer.
            </div>
          )}

          <div className="formactions">
            {!externalModalTested ? (
              <button
                className="btn btn-accent"
                type="button"
                onClick={testExternalModal}
                disabled={
                  !externalModalValue.trim()
                  || (externalModalRow?.inputKey2 && !externalModalValue2.trim())
                  || externalModalTesting
                }
              >
                {externalModalTesting ? "Test en cours..." : "Tester"}
              </button>
            ) : (
              <button className="btn btn-accent" type="button" onClick={saveExternalModal}>
                Enregistrer
              </button>
            )}
            <button className="btn" type="button" onClick={closeExternalModal} disabled={externalModalTesting}>
              Annuler
            </button>
          </div>
        </div>
      </Modal>

      <Modal
        open={externalDisableOpen}
        title={externalDisableRow ? `Désactiver : ${externalDisableRow.title}` : "Désactiver"}
        onClose={closeExternalDisable}
        width={520}
      >
        <div style={{ padding: 12 }}>
          <div style={{ fontWeight: 700, marginBottom: 8 }}>
            Confirmer la désactivation ?
          </div>
          <div className="muted" style={{ marginBottom: 12 }}>
            Cette action va désactiver le provider et supprimer la clé API associée.
          </div>
          <div className="formactions">
            <button className="btn btn-danger" type="button" onClick={confirmDisableExternal}>
              Désactiver
            </button>
            <button className="btn" type="button" onClick={closeExternalDisable}>
              Annuler
            </button>
          </div>
        </div>
      </Modal>

      <Modal
        open={externalToggleOpen}
        title={externalToggleRow ? `${externalToggleRow.enabled ? "Désactiver" : "Activer"} : ${externalToggleRow.title}` : "Activer/Désactiver"}
        onClose={closeExternalToggle}
        width={520}
      >
        <div style={{ padding: 12 }}>
          <div style={{ fontWeight: 700, marginBottom: 8 }}>
            Confirmer l’action ?
          </div>
          <div className="muted" style={{ marginBottom: 12 }}>
            {externalToggleRow?.enabled
              ? "Cette action va désactiver le provider (la clé API est conservée)."
              : "Cette action va activer le provider."}
          </div>
          <div className="formactions">
            <button className="btn" type="button" onClick={confirmToggleExternal}>
              {externalToggleRow?.enabled ? "Désactiver" : "Activer"}
            </button>
            <button className="btn" type="button" onClick={closeExternalToggle}>
              Annuler
            </button>
          </div>
        </div>
      </Modal>
    </>
  );
}
