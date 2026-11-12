import React from "react";
import { shadowIdentity } from "./matchingShadowModel.js";

export default function MatchingShadowEntry({ entry }) {
  return <details className="indexer-card">
        <summary>#{entry.releaseId} · {entry.rawTitle || "Titre indisponible"} · V2 {entry.decision}{entry.divergence ? " · divergence" : ""}</summary>
        {entry.rawTitleMaskReason && <p>Motif du masquage : {entry.rawTitleMaskReason}</p>}
        <p>Historique : {entry.historicalDecision} {shadowIdentity(entry.historicalIdentity)} (score {entry.historicalScore ?? "—"})</p>
        <p>V2 : {entry.decision} {shadowIdentity(entry.selectedIdentity)} · {Number(entry.durationMs).toFixed(2)} ms{entry.preserveExisting ? " · identité protégée" : ""}</p>
        <p>Raisons / informations manquantes : {entry.reasons.join(", ") || "—"}</p>
        <ul>{entry.queries.map((query, i) => <li key={i}>Requête candidate : {query.title} · année {query.year ?? "inconnue"} ({query.yearEvidence})</li>)}</ul>
        <ul>{entry.candidates.map((candidate, i) => <li key={i}>
          {candidate.title} ({candidate.year ?? "année inconnue"}) · {shadowIdentity(candidate.identity)} · {candidate.decision} · confiance {candidate.confidence.toFixed(3)}
          <div>Raisons : {candidate.reasons.join(", ") || "—"}</div>
          <ul>{(candidate.officialTitles || []).map((title, j) => <li key={j}>
            Titre provider : {title.title} · {title.kind} · {title.language || "langue de réponse non spécifiée"} · {title.source}{title.maskReason ? ` · masquage : ${title.maskReason}` : ""}
          </li>)}</ul>
          {(candidate.compoundTitles || []).length > 0 && <div>Titre composé prouvé :
            <ul>{candidate.compoundTitles.map((title, j) => <li key={j}>{title.title} · {title.kind} · {title.source} · {title.language || "locale inconnue"}{title.maskReason ? ` · masquage : ${title.maskReason}` : ""}</li>)}</ul>
          </div>}
          {candidate.trailingYearHypothesis != null && <div>Année finale hypothétique : {candidate.trailingYearHypothesis} — aucune année confirmée, acceptation bloquée.</div>}
          <div>Contradictions : {candidate.contradictions.join(", ") || "—"}</div>
        </li>)}</ul>
      </details>;
}
