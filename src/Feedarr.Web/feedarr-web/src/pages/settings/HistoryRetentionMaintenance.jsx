import React, { useRef, useState } from "react";
import { apiGet, apiPost } from "../../api/client.js";
import { fmtBytes } from "./settingsUtils.js";

const protectionLabels = {
  "entity:release": "Entities utilisées par une release",
  "entity:manual_correction": "Corrections manuelles",
  "entity:unknown_provenance": "Provenance historique inconnue",
  "entity:arr_association": "Associations Arr conservées",
  "entity:other_dependency": "Autres dépendances métier",
  "entity:request_identity": "Identités de requêtes conservées",
  "entity:manual_metadata": "Métadonnées ou posters manuels",
  "entity:unknown_data": "Données non reconnues à préserver",
  "entity:grace_period": "Entities dans le délai de grâce",
  "poster:active_publication": "Publication en cours",
  "poster:database_reference": "Posters encore référencés (entities, releases ou cache)",
  "poster:empty_or_nonregular_file": "Fichiers vides ou atypiques",
  "poster:unknown_or_manual_file": "Fichiers inconnus ou manuels",
  "poster:symlink": "Liens symboliques",
  "poster:file_grace_period": "Fichiers récents",
  "poster:pending_restore": "Restauration protégée",
  "poster:changed_before_delete": "Fichiers modifiés entre analyse et nettoyage",
  "poster:referenced_before_delete": "Références apparues avant suppression",
};

export default function HistoryRetentionMaintenance({ hidden = false, disabled = false, onBusyChange }) {
  const [report, setReport] = useState(null);
  const [busy, setBusy] = useState(false);
  const [confirmed, setConfirmed] = useState(false);
  const [error, setError] = useState("");
  const submitting = useRef(false);
  const run = async (execute = false, next = false) => {
    if (submitting.current || disabled) return;
    if (execute && (!confirmed || !window.confirm("Nettoyer au maximum 100 entities et 100 posters éligibles ? Les références seront vérifiées à nouveau."))) return;
    submitting.current = true; setBusy(true); onBusyChange?.(true); setError("");
    try {
      const request = { dryRun: !execute, limit: 100, scanLimit: 1000,
        afterPoster: execute ? report?.windowAfterPoster : (next ? report?.nextPoster : null), afterStore: execute ? report?.windowAfterStore : (next ? report?.nextStore : null) };
      const query = new URLSearchParams({ limit: "100", scanLimit: "1000" });
      if (request.afterPoster) query.set("afterPoster", request.afterPoster);
      if (request.afterStore) query.set("afterStore", request.afterStore);
      const result = execute
        ? await apiPost("/api/maintenance/history-cleanup", { ...request, confirmation: "RECLAIM_UNUSED_HISTORY" }, { timeoutMs: 120000 })
        : await apiGet(`/api/maintenance/history-cleanup?${query}`, { timeoutMs: 120000 });
      if (!result || typeof result.entityCandidates !== "number") throw new Error("Rapport de nettoyage invalide.");
      setReport(result); setConfirmed(false);
    } catch (e) { setError(e?.message || "Analyse de l’historique indisponible."); }
    finally { submitting.current = false; setBusy(false); onBusyChange?.(false); }
  };
  // Keep reports and in-flight guards when advanced options are temporarily hidden.
  return <div className="settings-card settings-card--full" hidden={hidden}>
    <div className="indexer-row indexer-row--settings">
      <div>
        <div className="settings-card__title">Historique des entities et posters</div>
        <p className="muted">Simulation par défaut. Délai de grâce : 30 jours. Les anciennes entities dont la provenance est inconnue, les corrections manuelles et les références métier sont conservées.</p>
      </div>
      <div className="indexer-actions">
        <button type="button" className="btn" disabled={disabled || busy} onClick={() => run()}>{busy ? "Analyse en cours…" : "Analyser sans supprimer"}</button>
      </div>
    </div>
    {error && <p role="alert" className="onboarding__error">{error}</p>}
    {report && <>
      <dl>
        <dt>Entities candidates (total / lot)</dt><dd>{report.entityCandidates} / {report.entityBatchCandidates}</dd>
        <dt>Entities protégées</dt><dd>{report.protectedEntities}</dd>
        <dt>Posters candidats / examinés (fenêtre courante)</dt><dd>{report.posterCandidates} / {report.postersScanned}</dd>
        <dt>Volume estimé récupérable dans cette fenêtre</dt><dd>{fmtBytes(report.estimatedRecoverableBytes)}</dd>
        <dt>Posters protégés</dt><dd>{report.protectedPosters}</dd>
        <dt>Stores déclarés sans répertoire (fenêtre courante)</dt><dd>{report.missingStoreDirectories} ({report.missingStoreReferenceRows} références)</dd>
        <dt>Erreurs / fichiers déjà absents</dt><dd>{report.errors} / {report.missingPosterFiles}</dd>
        {!report.dryRun && <><dt>Supprimés : entities / posters / volume logique</dt><dd>{report.entitiesDeleted} / {report.postersDeleted} / {fmtBytes(report.reclaimedBytes)}</dd></>}
      </dl>
      <details><summary>Raisons de protection</summary><ul>{Object.entries(report.protectionReasons || {}).map(([reason, count]) => <li key={reason}>{protectionLabels[reason] || reason} : {count}</li>)}</ul></details>
      {(report.restoreProtected || report.publicationBusy) && <p role="status">Nettoyage différé : restauration ou publication de poster protégée. Aucune suppression ne sera forcée.</p>}
      {(report.nextPoster || report.nextStore) && <button type="button" className="btn" disabled={disabled || busy} onClick={() => run(false, true)}>Analyser la fenêtre suivante</button>}
      <p className="muted">Les volumes sont les tailles logiques des fichiers ; le gain disque dépend notamment des snapshots. Les compteurs de fichiers et de volume sont partiels : au maximum 1 000 fichiers et 1 000 stores examinés par fenêtre. Le nettoyage réel réévalue la fenêtre analysée.</p>
      <label><input type="checkbox" checked={confirmed} disabled={disabled || busy} onChange={e => setConfirmed(e.target.checked)} /> Activer explicitement un nettoyage réel borné (100 entities et 100 posters maximum).</label>
      <button type="button" className="btn btn-danger" disabled={disabled || busy || !confirmed || report.restoreProtected || report.errors > 0} onClick={() => run(true)}>Confirmer le nettoyage du lot</button>
    </>}
  </div>;
}
