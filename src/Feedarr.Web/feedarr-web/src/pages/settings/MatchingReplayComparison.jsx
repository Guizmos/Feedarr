import React, { useRef, useState } from "react";
import { compareReplayReports, parseReplayReport, replayReportMaxBytes } from "./matchingReplayComparison.js";
import { replayDifference } from "./matchingReplayModel.js";

const labels = { changed: "projection provider modifiée", unchanged: "projection provider identique", unavailable: "preuve absente",
  not_comparable_coverage: "couverture incomplète", not_comparable_context: "contexte inconnu", query_context_changed: "requêtes ou locale différentes", projection_version_changed: "version de projection différente" };
export default function MatchingReplayComparison() {
  const sequence = useRef(0);
  const [comparison, setComparison] = useState(null);
  const [error, setError] = useState("");
  const compare = async event => {
    const version = ++sequence.current;
    const files = [...event.target.files]; setError(""); setComparison(null);
    try {
      if (files.length !== 2 || files.some(file => file.size > replayReportMaxBytes)) throw new Error();
      const reports = await Promise.all(files.map(async file => parseReplayReport(await file.text())));
      if (version === sequence.current) setComparison(compareReplayReports(...reports));
    } catch { if (version === sequence.current) setError("Choisissez deux exports Replay JSON valides, de 10 Mo maximum chacun. Aucun fichier n’est envoyé au serveur."); }
  };
  return <details>
    <summary>Comparer deux rapports JSON hors ligne</summary>
    <p>Comparaison locale au navigateur, sans upload ni appel provider. Les variations portent sur la projection utile au matching, pas sur la réponse brute. Une association historique reste non vérifiée.</p>
    <input type="file" accept=".json,application/json" multiple aria-label="Deux rapports Replay JSON" onChange={compare} />
    {error && <p role="alert">{error}</p>}
    {comparison && <>
      <p>Décisions : {comparison.decisions} · couverture : {comparison.coverage} · identités proposées : {comparison.identities} · projections providers modifiées : {comparison.providerChanges} · non comparables : {comparison.notComparable}</p>
      <ul>{comparison.entries.map(entry => <li key={entry.id}>#{entry.id} · {({ removed: "absent du second", added: "ajouté au second", both: "présent dans les deux" })[entry.presence]} · décision : {replayDifference(entry.decisionChanged)} · couverture : {replayDifference(entry.coverageChanged)} · identité : {replayDifference(entry.identityChanged)} · {labels[entry.providerEvidence]}</li>)}</ul>
    </>}
  </details>;
}
