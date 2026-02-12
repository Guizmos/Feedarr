import React from "react";
import Modal from "../../ui/Modal.jsx";
import AppIcon from "../../ui/AppIcon.jsx";
import { fmtBytes } from "./settingsUtils.js";

function fmtUptime(seconds) {
  if (!seconds || seconds <= 0) return "0s";
  const d = Math.floor(seconds / 86400);
  const h = Math.floor((seconds % 86400) / 3600);
  const m = Math.floor((seconds % 3600) / 60);
  if (d > 0) return `${d}j ${h}h ${m}m`;
  if (h > 0) return `${h}h ${m}m`;
  return `${m}m`;
}

function BtnSpinner() {
  return <AppIcon name="progress_activity" className="iconbtn--spin" size={14} style={{ marginRight: 6, verticalAlign: "middle" }} />;
}

function ActionRow({ label, description, buttonLabel, loadingLabel, loading, disabled, onClick, result }) {
  return (
    <div className="indexer-card">
      <div className="indexer-row indexer-row--settings">
        <div>
          <span className="indexer-title">{label}</span>
          {description && <div className="muted" style={{ fontSize: 12, marginTop: 2 }}>{description}</div>}
          {result && <div className="muted" style={{ fontSize: 12, marginTop: 4, color: "var(--success, #4caf50)" }}>{result}</div>}
        </div>
        <div className="indexer-actions">
          <button className="btn" type="button" onClick={onClick} disabled={loading || disabled}>
            {loading && <BtnSpinner />}
            {loading ? (loadingLabel || "...") : (buttonLabel || "Exécuter")}
          </button>
        </div>
      </div>
    </div>
  );
}

export default function SettingsMaintenance({
  // Cache
  clearCacheOpen, clearCacheLoading, setClearCacheOpen, handleClearCache,
  // Selective purge
  purgeSelectiveOpen, setPurgeSelectiveOpen, purgeSelectiveLoading,
  purgeScope, setPurgeScope, purgeOlderThanDays, setPurgeOlderThanDays,
  purgeSelectiveResult, handlePurgeSelective,
  // Vacuum
  vacuumOpen, setVacuumOpen, vacuumLoading, vacuumResult, handleVacuum,
  // Stats
  stats, statsLoading,
  // Orphan cleanup
  cleanupPostersOpen, setCleanupPostersOpen, cleanupPostersLoading, cleanupPostersResult, handleCleanupPosters,
  // Test providers
  testProvidersLoading, testProvidersResults, handleTestProviders,
  // Reparse
  reparseOpen, setReparseOpen, reparseLoading, reparseResult, handleReparse,
  // Duplicates
  duplicatesLoading, duplicatesResult, handleDetectDuplicates,
  duplicatesPurgeOpen, setDuplicatesPurgeOpen, duplicatesPurgeLoading, handlePurgeDuplicates,
}) {
  const anyLoading = clearCacheLoading || purgeSelectiveLoading || vacuumLoading
    || cleanupPostersLoading || testProvidersLoading || reparseLoading
    || duplicatesLoading || duplicatesPurgeLoading;

  return (
    <>
      {/* MAINTENANCE ACTIONS CARD */}
      <div className="settings-card settings-card--full" id="maintenance">
        <div className="settings-card__title">Actions de maintenance</div>

        {/* 1. Vider cache posters */}
        <ActionRow
          label="Vider cache posters"
          description="Supprime tous les posters téléchargés localement"
          loading={clearCacheLoading}
          loadingLabel="Suppression..."
          onClick={() => setClearCacheOpen(true)}
          disabled={anyLoading || (stats && stats.posterCount === 0)}
        />

        {/* 2. Purger logs (sélectif) */}
        <ActionRow
          label="Purger logs"
          description="Supprime les logs d'activité (tous, historique ou logs uniquement)"
          loading={purgeSelectiveLoading}
          loadingLabel="Suppression..."
          onClick={() => setPurgeSelectiveOpen(true)}
          disabled={anyLoading || (stats && stats.activityCount === 0)}
          result={purgeSelectiveResult?.deleted >= 0 ? `${purgeSelectiveResult.deleted} entrées supprimées` : null}
        />

        {/* 3. Optimiser DB */}
        <ActionRow
          label="Optimiser la base de données"
          description="Compacte la base SQLite (VACUUM) pour récupérer de l'espace"
          loading={vacuumLoading}
          loadingLabel="Optimisation..."
          onClick={() => setVacuumOpen(true)}
          disabled={anyLoading}
          result={vacuumResult ? `${vacuumResult.dbSizeBefore} MB → ${vacuumResult.dbSizeAfter} MB (${vacuumResult.savedMB} MB récupérés)` : null}
        />

        {/* 4. Nettoyer posters orphelins */}
        <ActionRow
          label="Nettoyer posters orphelins"
          description="Supprime les fichiers de posters non référencés en base"
          loading={cleanupPostersLoading}
          loadingLabel="Nettoyage..."
          onClick={() => setCleanupPostersOpen(true)}
          disabled={anyLoading || (stats && stats.orphanedPosterCount === 0)}
          result={cleanupPostersResult ? `${cleanupPostersResult.deleted} supprimés / ${cleanupPostersResult.scanned} scannés (${fmtBytes(cleanupPostersResult.freedBytes) || "0 B"} libérés)` : null}
        />

        {/* 5. Tester providers */}
        <div className="indexer-card">
          <div className="indexer-row indexer-row--settings">
            <div>
              <span className="indexer-title">Tester connectivité providers</span>
              <div className="muted" style={{ fontSize: 12, marginTop: 2 }}>Vérifie la connexion TMDB, TvMaze, Fanart, IGDB</div>
              {testProvidersResults && (
                <div style={{ marginTop: 6, display: "flex", gap: 12, flexWrap: "wrap" }}>
                  {testProvidersResults.map((r) => (
                    <span key={r.provider} style={{
                      fontSize: 12,
                      padding: "2px 8px",
                      borderRadius: 4,
                      background: r.ok ? "var(--success-bg, rgba(76,175,80,0.15))" : "var(--danger-bg, rgba(244,67,54,0.15))",
                      color: r.ok ? "var(--success, #4caf50)" : "var(--danger, #f44336)",
                    }}>
                      {r.provider.toUpperCase()} {r.ok ? "OK" : "ERREUR"} ({r.elapsedMs}ms)
                    </span>
                  ))}
                </div>
              )}
            </div>
            <div className="indexer-actions">
              <button className="btn" type="button" onClick={handleTestProviders} disabled={testProvidersLoading || anyLoading}>
                {testProvidersLoading && <BtnSpinner />}
                {testProvidersLoading ? "Test..." : "Tester"}
              </button>
            </div>
          </div>
        </div>

        {/* 6. Re-parser les titres */}
        <ActionRow
          label="Re-parser les titres"
          description="Recalcule les métadonnées de toutes les releases (titre, saison, résolution...)"
          loading={reparseLoading}
          loadingLabel="Re-parsing..."
          onClick={() => setReparseOpen(true)}
          disabled={anyLoading || (stats && stats.releasesCount === 0)}
          result={reparseResult ? `${reparseResult.updated} / ${reparseResult.total} releases mises à jour` : null}
        />

        {/* 7. Doublons */}
        <div className="indexer-card">
          <div className="indexer-row indexer-row--settings">
            <div>
              <span className="indexer-title">Détecter les doublons</span>
              <div className="muted" style={{ fontSize: 12, marginTop: 2 }}>Identifie et supprime les releases en double par source</div>
              {duplicatesResult && !duplicatesResult.purged && (
                <div className="muted" style={{ fontSize: 12, marginTop: 4, color: "var(--warning, #ff9800)" }}>
                  {duplicatesResult.groupsFound} groupes, {duplicatesResult.duplicatesCount} doublons détectés
                </div>
              )}
              {duplicatesResult?.purged && (
                <div className="muted" style={{ fontSize: 12, marginTop: 4, color: "var(--success, #4caf50)" }}>
                  {duplicatesResult.deleted} doublons supprimés
                </div>
              )}
            </div>
            <div className="indexer-actions" style={{ display: "flex", gap: 6 }}>
              <button className="btn" type="button" onClick={handleDetectDuplicates} disabled={duplicatesLoading || anyLoading}>
                {duplicatesLoading && <BtnSpinner />}
                {duplicatesLoading ? "Analyse..." : "Analyser"}
              </button>
              {duplicatesResult && duplicatesResult.duplicatesCount > 0 && !duplicatesResult.purged && (
                <button className="btn btn-danger" type="button" onClick={() => setDuplicatesPurgeOpen(true)} disabled={duplicatesPurgeLoading || anyLoading}>
                  Purger
                </button>
              )}
            </div>
          </div>
        </div>
      </div>

      {/* STATS CARD */}
      <div className="settings-card settings-card--full" id="maintenance-stats">
        <div className="settings-card__title">Statistiques</div>
        {statsLoading && <div className="muted" style={{ padding: "8px 0" }}>Chargement...</div>}
        {!statsLoading && stats && (
          <div className="stats-table table">
            <div className="thead">
              <div className="th">Métrique</div>
              <div className="th td-value">Valeur</div>
            </div>
            {[
              ["Releases en base", stats.releasesCount?.toLocaleString() ?? "—"],
              ["Sources", `${stats.activeSourcesCount ?? 0} actives / ${stats.sourcesCount ?? 0} total`],
              ["Taille base de données", `${stats.dbSizeMB ?? 0} MB`],
              ["Posters en cache", `${stats.posterCount ?? 0} (${stats.posterSizeMB ?? 0} MB)`],
              ["Posters manquants", stats.missingPosterCount?.toLocaleString() ?? "0"],
              ["Posters orphelins", stats.orphanedPosterCount?.toLocaleString() ?? "0"],
              ["Cache de matching", `${stats.matchCacheCount?.toLocaleString() ?? 0} entrées`],
              ["Entités média", stats.mediaEntityCount?.toLocaleString() ?? "0"],
              ["Doublons détectés", stats.duplicateCount?.toLocaleString() ?? "0"],
              ["Logs d'activité", stats.activityCount?.toLocaleString() ?? "0"],
              ["Uptime serveur", fmtUptime(stats.uptimeSeconds)],
            ].map(([label, value]) => (
              <div className="trow" key={label}>
                <div className="td">{label}</div>
                <div className="td td-value">{value}</div>
              </div>
            ))}
            {stats.releasesPerCategory?.length > 0 && (
              <div className="trow">
                <div className="td">Par catégorie</div>
                <div className="td td-categories">
                  {stats.releasesPerCategory.map((c) => (
                    <span key={c.category} className="cat-bubble">{c.category}: {c.count}</span>
                  ))}
                </div>
              </div>
            )}
          </div>
        )}
      </div>

      {/* MODAL: CLEAR CACHE */}
      <Modal open={clearCacheOpen} title="Vider le cache posters" onClose={() => setClearCacheOpen(false)} width={520}>
        <div style={{ padding: 12 }}>
          <div style={{ fontWeight: 700, marginBottom: 8 }}>Confirmer la suppression ?</div>
          <div className="muted" style={{ marginBottom: 12 }}>
            Cette action va supprimer tous les posters téléchargés localement.
            Ils seront re-téléchargés automatiquement lors du prochain affichage.
          </div>
          <div className="formactions">
            <button className="btn btn-danger" type="button" onClick={handleClearCache} disabled={clearCacheLoading}>
              {clearCacheLoading && <BtnSpinner />}{clearCacheLoading ? "Suppression..." : "Confirmer"}
            </button>
            <button className="btn" type="button" onClick={() => setClearCacheOpen(false)} disabled={clearCacheLoading}>Annuler</button>
          </div>
        </div>
      </Modal>

      {/* MODAL: SELECTIVE PURGE LOGS */}
      <Modal open={purgeSelectiveOpen} title="Purger les logs" onClose={() => setPurgeSelectiveOpen(false)} width={520}>
        <div style={{ padding: 12 }}>
          <div style={{ fontWeight: 700, marginBottom: 8 }}>Options de purge</div>
          <div className="field" style={{ marginBottom: 12 }}>
            <label className="muted" style={{ display: "block", marginBottom: 4 }}>Scope</label>
            <select value={purgeScope} onChange={(e) => setPurgeScope(e.target.value)} style={{ width: "100%" }}>
              <option value="all">Tous les logs</option>
              <option value="history">Historique (sync uniquement)</option>
              <option value="logs">Logs (hors sync)</option>
            </select>
          </div>
          <div className="field" style={{ marginBottom: 12 }}>
            <label className="muted" style={{ display: "block", marginBottom: 4 }}>Plus vieux que (jours) — laisser vide pour tout supprimer</label>
            <input
              type="number"
              min="1"
              placeholder="ex: 30"
              value={purgeOlderThanDays}
              onChange={(e) => setPurgeOlderThanDays(e.target.value)}
              style={{ width: "100%" }}
            />
          </div>
          <div className="formactions">
            <button className="btn btn-danger" type="button" onClick={handlePurgeSelective} disabled={purgeSelectiveLoading}>
              {purgeSelectiveLoading && <BtnSpinner />}{purgeSelectiveLoading ? "Suppression..." : "Purger"}
            </button>
            <button className="btn" type="button" onClick={() => setPurgeSelectiveOpen(false)} disabled={purgeSelectiveLoading}>Annuler</button>
          </div>
        </div>
      </Modal>

      {/* MODAL: VACUUM */}
      <Modal open={vacuumOpen} title="Optimiser la base de données" onClose={() => setVacuumOpen(false)} width={520}>
        <div style={{ padding: 12 }}>
          <div style={{ fontWeight: 700, marginBottom: 8 }}>Confirmer l'optimisation ?</div>
          <div className="muted" style={{ marginBottom: 12 }}>
            Cette action exécute un VACUUM SQLite pour compacter la base de données et récupérer l'espace disque.
            L'opération peut prendre quelques secondes.
          </div>
          <div className="formactions">
            <button className="btn btn-accent" type="button" onClick={handleVacuum} disabled={vacuumLoading}>
              {vacuumLoading && <BtnSpinner />}{vacuumLoading ? "Optimisation..." : "Confirmer"}
            </button>
            <button className="btn" type="button" onClick={() => setVacuumOpen(false)} disabled={vacuumLoading}>Annuler</button>
          </div>
        </div>
      </Modal>

      {/* MODAL: CLEANUP POSTERS */}
      <Modal open={cleanupPostersOpen} title="Nettoyer les posters orphelins" onClose={() => setCleanupPostersOpen(false)} width={520}>
        <div style={{ padding: 12 }}>
          <div style={{ fontWeight: 700, marginBottom: 8 }}>Confirmer le nettoyage ?</div>
          <div className="muted" style={{ marginBottom: 12 }}>
            Cette action va scanner le dossier de posters et supprimer les fichiers qui ne sont plus
            référencés par aucune release en base. Cette action est définitive.
          </div>
          <div className="formactions">
            <button className="btn btn-danger" type="button" onClick={handleCleanupPosters} disabled={cleanupPostersLoading}>
              {cleanupPostersLoading && <BtnSpinner />}{cleanupPostersLoading ? "Nettoyage..." : "Confirmer"}
            </button>
            <button className="btn" type="button" onClick={() => setCleanupPostersOpen(false)} disabled={cleanupPostersLoading}>Annuler</button>
          </div>
        </div>
      </Modal>

      {/* MODAL: REPARSE */}
      <Modal open={reparseOpen} title="Re-parser les titres" onClose={() => setReparseOpen(false)} width={520}>
        <div style={{ padding: 12 }}>
          <div style={{ fontWeight: 700, marginBottom: 8 }}>Confirmer le re-parsing ?</div>
          <div className="muted" style={{ marginBottom: 12 }}>
            Cette action va re-parser tous les titres de releases pour recalculer les métadonnées
            (titre nettoyé, saison, épisode, résolution, codec, etc.). Utile après une mise à jour du parser.
            L'opération peut prendre un moment selon le nombre de releases.
          </div>
          <div className="formactions">
            <button className="btn btn-accent" type="button" onClick={handleReparse} disabled={reparseLoading}>
              {reparseLoading && <BtnSpinner />}{reparseLoading ? "Re-parsing..." : "Confirmer"}
            </button>
            <button className="btn" type="button" onClick={() => setReparseOpen(false)} disabled={reparseLoading}>Annuler</button>
          </div>
        </div>
      </Modal>

      {/* MODAL: PURGE DUPLICATES */}
      <Modal open={duplicatesPurgeOpen} title="Purger les doublons" onClose={() => setDuplicatesPurgeOpen(false)} width={520}>
        <div style={{ padding: 12 }}>
          <div style={{ fontWeight: 700, marginBottom: 8 }}>Confirmer la suppression des doublons ?</div>
          <div className="muted" style={{ marginBottom: 12 }}>
            Cette action va supprimer {duplicatesResult?.duplicatesCount ?? 0} releases en double.
            Pour chaque groupe de doublons, seule la release la plus récente sera conservée.
            Cette action est définitive.
          </div>
          <div className="formactions">
            <button className="btn btn-danger" type="button" onClick={handlePurgeDuplicates} disabled={duplicatesPurgeLoading}>
              {duplicatesPurgeLoading && <BtnSpinner />}{duplicatesPurgeLoading ? "Suppression..." : "Purger"}
            </button>
            <button className="btn" type="button" onClick={() => setDuplicatesPurgeOpen(false)} disabled={duplicatesPurgeLoading}>Annuler</button>
          </div>
        </div>
      </Modal>
    </>
  );
}
