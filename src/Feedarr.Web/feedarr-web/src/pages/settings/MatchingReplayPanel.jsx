import React, { useCallback, useEffect, useRef, useState } from "react";
import { apiGet, apiPost } from "../../api/client.js";
import MatchingShadowEntry from "./MatchingShadowEntry.jsx";
import { shadowCounts } from "./matchingShadowModel.js";
import MatchingReplayComparison from "./MatchingReplayComparison.jsx";
import { replayPayload, replayDifference, replayCategories, replayCategoryLabels, parseReplayIds } from "./matchingReplayModel.js";

const endpoint = "/api/diagnostics/matching-shadow/replay";
export default function MatchingReplayPanel() {
  const [open, setOpen] = useState(false);
  const [report, setReport] = useState(null);
  const [count, setCount] = useState(10);
  const [mode, setMode] = useState("offline");
  const [budget, setBudget] = useState(30);
  const [method, setMethod] = useState("sample");
  const [categories, setCategories] = useState([...replayCategories]);
  const [ids, setIds] = useState("");
  const [filter, setFilter] = useState("all");
  const [fairBudget, setFairBudget] = useState(false);
  const [preview, setPreview] = useState(null);
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
      const selection = kind === "start" || kind === "preview" ? { method, categories, releaseIds: method === "ids" ? parseReplayIds(ids) : [], filter, fairBudget } : null;
      const next = kind === "start" || kind === "preview"
        ? await apiPost(kind === "preview" ? `${endpoint}/preview` : endpoint,
          replayPayload(count, mode, budget, kind === "preview" ? report.generation : preview.generation, selection, kind === "start" ? preview.selectedIds : undefined))
        : await apiPost(`${endpoint}/${kind}`, {});
      if (mounted.current && version === sequence.current) {
        if (kind === "preview") setPreview(next);
        else { setReport(next); setPreview(null); }
      }
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
    <label>Sélection <select value={method} disabled={busy || report?.running} onChange={e => { setMethod(e.target.value); setPreview(null); }}>
      <option value="sample">Échantillon déterministe</option><option value="ids">IDs explicites</option><option value="shadow_divergences">Divergences Shadow en mémoire</option>
    </select></label>
    {method === "ids" && <label>IDs (200 maximum, séparés par virgules, espaces ou points-virgules)
      <textarea value={ids} maxLength={4000} disabled={busy || report?.running} onChange={e => { setIds(e.target.value); setPreview(null); }} />
    </label>}
    <fieldset disabled={busy || report?.running}><legend>Catégories</legend>
      {replayCategories.map(category => <label key={category}><input type="checkbox" checked={categories.includes(category)} onChange={e => {
        setCategories(e.target.checked ? [...categories, category] : categories.filter(c => c !== category)); setPreview(null);
      }} />{replayCategoryLabels[category]}</label>)}
    </fieldset>
    <label>Filtre <select value={filter} disabled={busy || report?.running} onChange={e => { setFilter(e.target.value); setPreview(null); }}>
      <option value="all">Toutes</option><option value="without_poster">Sans poster</option><option value="without_identity">Sans identité</option><option value="with_identity">Avec identité</option>
    </select></label>{" "}
    <label>Releases <select value={count} disabled={method === "ids" || busy || report?.running} onChange={e => { setCount(Number(e.target.value)); setPreview(null); }}>
      {[10, 25, 50, 100, 200].map(n => <option key={n} value={n}>{n}</option>)}
    </select></label>{" "}
    <label>Mode <select value={mode} disabled={busy || report?.running} onChange={e => { setMode(e.target.value); setPreview(null); }}>
      <option value="offline">Hors ligne</option><option value="tmdb">Recherche TMDB</option>
    </select></label>{" "}
    <label>Budget HTTP (1–30) <input type="number" min="1" max="30" value={budget}
      disabled={mode !== "tmdb" || busy || report?.running} onChange={e => { setBudget(Number(e.target.value)); setPreview(null); }} /></label>
    <label><input type="checkbox" checked={fairBudget} disabled={busy || report?.running} onChange={e => { setFairBudget(e.target.checked); setPreview(null); }} /> Répartir équitablement le budget entre les catégories présentes</label>
    <p>{mode === "offline" ? "Hors ligne : aucun appel provider. Sans réponse locale exploitable, le résultat reste not_evaluable."
      : "Recherche TMDB : appels diagnostiques explicitement autorisés au clic, pagination et réponses localisées incluses dans le budget. Aucun poster téléchargé."}</p>
    <div className="indexer-actions">
      <button type="button" className="btn" disabled={!report || busy || report.running} onClick={() => action("preview")}>Estimer sans appel provider</button>
      <button type="button" className="btn" disabled={!report || busy || report.running || !preview?.selectedIds.length || preview.generation !== report.generation} onClick={() => action("start")}>Rejouer le matching</button>
      <button type="button" className="btn" disabled={!report?.selectedIds?.length || busy || report.running} onClick={() => {
        setMethod("ids"); setIds(report.selectedIds.join(", ")); setCategories([...replayCategories]); setFilter("all"); setPreview(null);
      }}>Reprendre les mêmes IDs sélectionnés</button>
      <button type="button" className="btn" disabled={!report} onClick={() => action("stop")}>Arrêter le replay</button>
      <button type="button" className="btn" disabled={!report || busy} onClick={() => action("reset")}>Effacer la session</button>
      <button type="button" className="btn" disabled={busy} onClick={load}>Actualiser</button>
      <button type="button" className="btn" disabled={!report?.sessionId} onClick={download}>Exporter JSON</button>
    </div>
    {preview && <div role="status">
      <p>{preview.selectedIds.length} releases · minimum estimé : {preview.minimumRequests} requêtes / budget {preview.budget}.
        {preview.paginationUnknown ? " Pagination, redirections et erreurs restent inconnues ; aucune couverture complète n’est garantie." : " Aucun appel provider prévu."}
        {preview.minimumExceedsBudget ? " Le minimum dépasse le budget global ou un quota de catégorie." : ""}</p>
      <p>IDs figés pour le lancement : {preview.selectedIds.join(", ") || "aucun"}</p>
      <ul>{preview.categoryBudgets.map(group => <li key={group.category}>{replayCategoryLabels[group.category] || group.category} : {group.selected} releases · minimum {group.minimumRequests} · quota {group.limit ?? "budget commun"}</li>)}</ul>
    </div>}
    <MatchingReplayComparison />
    {error && <p role="alert">{error}</p>}
    {report && <>
      <p role="status">État : {report.status} · {report.processed}/{report.total} traitées · erreurs : {report.errors} · HTTP : {report.requestsUsed}/{report.requestBudget}</p>
      {report.selectedIds && <p>IDs sélectionnés : {report.selectedIds.join(", ") || "aucun"} · IDs traités : {(report.processedIds || []).join(", ") || "aucun"}</p>}
      <ul>{(report.categoryBudgets || []).map(group => <li key={group.category}>{replayCategoryLabels[group.category] || group.category} : {group.used} requête(s) · quota {group.limit ?? "commun"}</li>)}</ul>
      <progress value={report.processed} max={report.total || count} aria-label="Progression du replay" />
      {report.sessionError && <p role="alert">{report.sessionError}</p>}
      <p>{shadowCounts(report.summary)} · divergences : {report.summary.divergences} · durée moyenne : {report.summary.averageDurationMs.toFixed(2)} ms (traitement et réseau)</p>
      <ul>{report.distribution.map(group => <li key={group.category}>{replayCategoryLabels[group.category] || group.category} : {group.count}, sans poster {group.withoutPoster}, sans identité {group.withoutIdentity}, protégées {group.protected}</li>)}</ul>
      <p className="muted">Catégories TMDB non ambiguës : films, séries, animation, émissions et spectacles. Les identités ne sont pas validées indépendamment par ce replay.</p>
      {report.entries.map((entry, i) => <div key={`${entry.detail.releaseId}-${i}`}>
        <p>{entry.category} · année locale : {entry.parsedYear ?? "inconnue"} · saison/épisode : {entry.season ?? "—"}/{entry.episode ?? "—"} · couverture : {entry.coverage} · {entry.requests} requête(s) · différence de décision : {replayDifference(entry.decisionDifference)} · différence d’identité : {replayDifference(entry.identityDifference)} · identité vérifiée : inconnue</p>
        <MatchingShadowEntry entry={entry.detail} />
      </div>)}
    </>}
  </details>;
}
