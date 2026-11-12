import React, { useCallback, useEffect, useRef, useState } from "react";
import { apiGet, apiPost, apiPut } from "../../api/client.js";
import MatchingReplayPanel from "./MatchingReplayPanel.jsx";
import MatchingShadowEntry from "./MatchingShadowEntry.jsx";
import { shadowCounts } from "./matchingShadowModel.js";

const endpoint = "/api/diagnostics/matching-shadow";

export default function MatchingShadowPanel() {
  const [report, setReport] = useState(null);
  const [error, setError] = useState("");
  const [busy, setBusy] = useState(false);
  const [divergencesOnly, setDivergencesOnly] = useState(true);
  const version = useRef(0);
  const mounted = useRef(false);
  const mutation = useRef(false);
  const load = useCallback(async () => {
    const current = ++version.current;
    try {
      const next = await apiGet(endpoint);
      if (mounted.current && current === version.current) { setReport(next); setError(""); }
    } catch { if (mounted.current && current === version.current) setError("Diagnostics Shadow indisponibles."); }
  }, []);
  useEffect(() => {
    mounted.current = true;
    void load();
    const timer = setInterval(() => { if (!mutation.current) void load(); }, 10000);
    return () => { mounted.current = false; clearInterval(timer); };
  }, [load]);
  const change = async (action) => {
    // Stop remains available during an enable/reset. Server generations invalidate stale enables and publications.
    if (mutation.current && action !== "stop") return;
    mutation.current = true;
    const current = ++version.current;
    setBusy(true); setError("");
    try {
      const next = action === "reset" ? await apiPost(`${endpoint}/reset`, {}) :
        await apiPut(endpoint, { enabled: action === "enable", generation: report?.generation });
      if (mounted.current && current === version.current) setReport(next);
    } catch { if (mounted.current && current === version.current) setError("Action Shadow non confirmée. Actualisez son état."); }
    finally {
      if (mounted.current && current === version.current) { mutation.current = false; setBusy(false); }
    }
  };
  const entries = (report?.entries || []).filter(entry => !divergencesOnly || entry.divergence);
  return <section className="settings-card settings-card--full" aria-label="Matching V2 Shadow Beta">
    <div className="settings-card__title">Matching V2 Shadow — Beta</div>
    <p>Observation uniquement. Le moteur historique conserve les associations. Le Shadow passif ne lance aucune recherche provider supplémentaire.</p>
    <p>État : <strong>{report ? (report.enabled ? "ON" : "OFF") : "Chargement…"}</strong>. Retour à OFF après redémarrage.</p>
    <div className="indexer-actions">
      <button type="button" className="btn" disabled={!report || busy || report.enabled} onClick={() => change("enable")}>Activer Matching V2 Shadow</button>
      <button type="button" className="btn" disabled={!report} onClick={() => change("stop")}>Arrêter immédiatement</button>
      <button type="button" className="btn" disabled={!report || busy} onClick={() => change("reset")}>Remettre les statistiques à zéro</button>
      <button type="button" className="btn" disabled={busy} onClick={load}>Actualiser</button>
    </div>
    <MatchingReplayPanel />
    {error && <p role="alert">{error}</p>}
    {report && <>
      <p>Fenêtre des {report.observations} dernières observations (maximum {report.capacity}), {report.releases} releases distinctes,
        {" "}{report.evaluated} évaluations exploitables sur {report.evaluatedReleases} releases. Durée moyenne : {Number(report.averageDurationMs).toFixed(2)} ms (lecture des verrous et classement, hors réseau).</p>
      <p>{shadowCounts(report)} · Divergences : {report.divergences}</p>
      <p className="muted">Couverture initiale : sélection TMDB fraîche, retours de cache et refresh manuel. Une réponse absente ou partielle reste not_evaluable.
        Les divergences sont des propositions à vérifier, sans preuve de précision. Aucun historique permanent.</p>
      <label><input type="checkbox" checked={divergencesOnly} onChange={e => setDivergencesOnly(e.target.checked)} /> Afficher uniquement les divergences</label>
      {entries.length === 0 && <p>Aucune observation dans ce filtre.</p>}
      {entries.map((entry, index) => <MatchingShadowEntry key={`${entry.releaseId}-${index}`} entry={entry} />)}
    </>}
  </section>;
}
