import React, { useCallback, useEffect, useRef, useState } from "react";
import { apiGet, apiPost } from "../../api/client.js";
import MatchingShadowEntry from "./MatchingShadowEntry.jsx";

const endpoint = "/api/diagnostics/matching/replay";

export default function MatchingReplayPanel() {
  const [ids, setIds] = useState("");
  const [report, setReport] = useState(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");
  const mounted = useRef(false);
  const load = useCallback(async () => {
    try {
      const next = await apiGet(endpoint);
      if (mounted.current) setReport(next);
    } catch { if (mounted.current) setError("Diagnostic indisponible."); }
  }, []);
  useEffect(() => {
    mounted.current = true;
    void load();
    return () => { mounted.current = false; };
  }, [load]);
  useEffect(() => {
    if (!report?.running) return undefined;
    const timer = setInterval(load, 1000);
    return () => clearInterval(timer);
  }, [report?.running, load]);
  const analyze = async () => {
    const selected = ids.trim().split(/[\s,;]+/).map(Number);
    if (!selected.length || selected.length > 10 || selected.some(id => !Number.isSafeInteger(id) || id < 1)) {
      setError("Indiquez de 1 à 10 IDs de releases, séparés par des virgules.");
      return;
    }
    setBusy(true); setError("");
    try {
      const next = await apiPost(endpoint, {
        count: 10, mode: "tmdb", budget: 30, generation: report.generation,
        selection: { method: "ids", releaseIds: [...new Set(selected)] },
      });
      if (mounted.current) setReport(next);
    } catch { if (mounted.current) setError("Analyse non confirmée. Actualisez avant de réessayer."); }
    finally { if (mounted.current) setBusy(false); }
  };
  const stop = async () => {
    setBusy(true);
    try {
      const next = await apiPost(`${endpoint}/stop`, { sessionId: report.sessionId, generation: report.generation });
      if (mounted.current) setReport(next);
    } catch { if (mounted.current) setError("Arrêt non confirmé. Actualisez l’état."); }
    finally { if (mounted.current) setBusy(false); }
  };
  return <section className="settings-card settings-card--full" aria-label="Diagnostic du matching">
    <div className="settings-card__title">Diagnostic du matching</div>
    <p className="muted">Analyse ciblée avec TMDB, sans modifier les associations, métadonnées ou posters.
      Les séries et autres catégories conservent leur pipeline de production ; cette analyse ne publie aucun résultat.</p>
    <label>IDs des releases (10 maximum)
      <input value={ids} maxLength={200} disabled={busy || report?.running} onChange={e => setIds(e.target.value)} placeholder="523, 403" />
    </label>
    <div className="indexer-actions">
      <button type="button" className="btn btn-primary" disabled={!report || busy || report.running} onClick={analyze}>Analyser</button>
      {report?.running && <button type="button" className="btn" disabled={busy} onClick={stop}>Arrêter</button>}
      <button type="button" className="btn" disabled={busy || report?.running} onClick={load}>Actualiser</button>
    </div>
    {error && <p role="alert">{error}</p>}
    {report?.sessionId && <>
      <p role="status">État : {report.status} · {report.processed}/{report.total} · erreurs : {report.errors} · requêtes : {report.requestsUsed}/{report.requestBudget}</p>
      {report.entries.map(entry => <MatchingShadowEntry key={entry.detail.releaseId} entry={entry.detail} />)}
    </>}
  </section>;
}
