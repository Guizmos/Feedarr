import React, { useCallback, useEffect, useRef, useState } from "react";
import { apiGet, apiPost } from "../../api/client.js";
import MatchingShadowEntry from "./MatchingShadowEntry.jsx";
import { shadowCounts } from "./matchingShadowModel.js";
import { replayPayload, replayDifference } from "./matchingReplayModel.js";

const endpoint = "/api/diagnostics/matching-shadow/replay";
export default function MatchingReplayPanel() {
  const [open, setOpen] = useState(false);
  const [report, setReport] = useState(null);
  const [count, setCount] = useState(10);
  const [mode, setMode] = useState("offline");
  const [budget, setBudget] = useState(30);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");
  const sequence = useRef(0);
  const mounted = useRef(false);
  const mutating = useRef(false);
  const load = useCallback(async () => {
    const version = ++sequence.current;
    try {
      const next = await apiGet(endpoint);
      if (mounted.current && sequence.current === version) setReport(next);
    } catch { if (mounted.current && sequence.current === version) setError("Replay indisponible."); }
  }, []);
  useEffect(() => {
    mounted.current = true;
    if (open) void load();
    const timer = open ? setInterval(() => { if (!mutating.current) void load(); }, 1000) : null;
    return () => { mounted.current = false; if (timer) clearInterval(timer); };
  }, [open, load]);
  const action = async (kind) => {
    if (mutating.current && kind !== "stop") return;
    const version = ++sequence.current;
    mutating.current = true; setBusy(true); setError("");
    try {
      const next = kind === "start"
        ? await apiPost(endpoint, replayPayload(count, mode, budget, report.generation))
        : await apiPost(`${endpoint}/${kind}`, {});
      if (mounted.current && version === sequence.current) setReport(next);
    } catch { if (mounted.current && version === sequence.current) setError("Action non confirmée ou session déjà active. Actualisez avant de réessayer."); }
    finally { if (mounted.current && version === sequence.current) { mutating.current = false; setBusy(false); } }
  };
  const download = async () => {
    try {
      const exported = await apiGet(`${endpoint}/export`);
      const url = URL.createObjectURL(new Blob([JSON.stringify(exported, null, 2)], { type: "application/json" }));
      const link = document.createElement("a"); link.href = url; link.download = "feedarr-matching-replay.json";
      document.body.append(link); link.click(); link.remove(); URL.revokeObjectURL(url);
    } catch { setError("Export du replay indisponible."); }
  };
  return <details onToggle={e => setOpen(e.currentTarget.open)}>
    <summary>Rejouer le matching — diagnostic contrôlé</summary>
    <p>Échantillon déterministe et équilibré de releases existantes. Aucune association, métadonnée ou cache métier ne sera modifié.</p>
    <p>Le replay est indépendant du Shadow passif. Une identité enregistrée reste une hypothèse à vérifier, y compris pour une correction manuelle.</p>
    <label>Releases <select value={count} disabled={busy || report?.running} onChange={e => setCount(Number(e.target.value))}>
      {[10, 25, 50, 100, 200].map(n => <option key={n} value={n}>{n}</option>)}
    </select></label>{" "}
    <label>Mode <select value={mode} disabled={busy || report?.running} onChange={e => setMode(e.target.value)}>
      <option value="offline">Hors ligne</option><option value="tmdb">Recherche TMDB</option>
    </select></label>{" "}
    <label>Budget HTTP (1–30) <input type="number" min="1" max="30" value={budget}
      disabled={mode !== "tmdb" || busy || report?.running} onChange={e => setBudget(Number(e.target.value))} /></label>
    <p>{mode === "offline" ? "Hors ligne : aucun appel provider. Sans réponse locale exploitable, le résultat reste not_evaluable."
      : "Recherche TMDB : appels diagnostiques explicitement autorisés au clic, pagination et réponses localisées incluses dans le budget. Aucun poster téléchargé."}</p>
    <div className="indexer-actions">
      <button type="button" className="btn" disabled={!report || busy || report.running} onClick={() => action("start")}>Rejouer le matching</button>
      <button type="button" className="btn" disabled={!report} onClick={() => action("stop")}>Arrêter le replay</button>
      <button type="button" className="btn" disabled={!report || busy} onClick={() => action("reset")}>Effacer la session</button>
      <button type="button" className="btn" disabled={busy} onClick={load}>Actualiser</button>
      <button type="button" className="btn" disabled={!report?.sessionId} onClick={download}>Exporter JSON</button>
    </div>
    {error && <p role="alert">{error}</p>}
    {report && <>
      <p role="status">État : {report.status} · {report.processed}/{report.total} traitées · erreurs : {report.errors} · HTTP : {report.requestsUsed}/{report.requestBudget}</p>
      <progress value={report.processed} max={report.total || count} aria-label="Progression du replay" />
      {report.sessionError && <p role="alert">{report.sessionError}</p>}
      <p>{shadowCounts(report.summary)} · divergences : {report.summary.divergences} · durée moyenne : {report.summary.averageDurationMs.toFixed(2)} ms (traitement et réseau)</p>
      <ul>{report.distribution.map(group => <li key={group.category}>{group.category} : {group.count}, sans poster {group.withoutPoster}, sans identité {group.withoutIdentity}, protégées {group.protected}</li>)}</ul>
      <p className="muted">Catégories TMDB non ambiguës : films, séries, animation, émissions et spectacles. Les identités ne sont pas validées indépendamment par ce replay.</p>
      {report.entries.map((entry, i) => <div key={`${entry.detail.releaseId}-${i}`}>
        <p>{entry.category} · année locale : {entry.parsedYear ?? "inconnue"} · saison/épisode : {entry.season ?? "—"}/{entry.episode ?? "—"} · couverture : {entry.coverage} · {entry.requests} requête(s) · différence de décision : {replayDifference(entry.decisionDifference)} · différence d’identité : {replayDifference(entry.identityDifference)} · identité vérifiée : inconnue</p>
        <MatchingShadowEntry entry={entry.detail} />
      </div>)}
    </>}
  </details>;
}
