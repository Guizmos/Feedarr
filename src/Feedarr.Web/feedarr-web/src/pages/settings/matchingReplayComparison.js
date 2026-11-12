export const replayReportMaxBytes = 10 * 1024 * 1024;
const value = (object, key) => object?.[key] ?? object?.[key[0].toUpperCase() + key.slice(1)];
const safeId = id => Number.isSafeInteger(id) && id > 0;
const fingerprint = text => text == null ? null : /^[a-f0-9]{64}$/i.test(text) ? text.toLowerCase() : (() => { throw new Error("report_fingerprint_invalid"); })();
export function parseReplayReport(text) {
  if (typeof text !== "string" || text.length > replayReportMaxBytes) throw new Error("report_too_large");
  const report = JSON.parse(text);
  const rows = value(report, "entries");
  if (!Array.isArray(rows) || rows.length > 200) throw new Error("report_entries_invalid");
  const seen = new Set();
  return rows.map(row => {
    const detail = value(row, "detail");
    const id = value(detail, "releaseId"); const decision = value(detail, "decision"); const coverage = value(row, "coverage");
    if (!safeId(id) || seen.has(id) || !["accept", "ambiguous", "reject", "not_evaluable"].includes(decision) || typeof coverage !== "string" || !/^[a-z0-9_]{1,100}$/.test(coverage)) throw new Error("report_entry_invalid");
    seen.add(id);
    const identity = value(detail, "selectedIdentity");
    let selected = null;
    if (identity != null) {
      const provider = value(identity, "provider"), type = value(identity, "type"), externalId = value(identity, "id");
      if (provider !== "tmdb" || !["movie", "tv"].includes(type) || typeof externalId !== "string" || !/^[1-9]\d{0,15}$/.test(externalId)) throw new Error("report_identity_invalid");
      selected = `${provider}:${type}:${externalId}`;
    }
    const locale = value(row, "searchLocale") ?? null;
    if (locale !== null && (typeof locale !== "string" || !/^[a-z]{2,3}(?:-[a-zA-Z0-9]{2,8}){0,2}$/.test(locale))) throw new Error("report_locale_invalid");
    const version = value(row, "providerEvidenceVersion") ?? null;
    if (version !== null && (!Number.isSafeInteger(version) || version < 1)) throw new Error("report_evidence_version_invalid");
    return { id, decision, coverage, selected, locale, version, evidence: fingerprint(value(row, "providerEvidenceFingerprint")),
      plan: fingerprint(value(row, "queryPlanFingerprint")), complete: value(row, "providerEvidenceComplete") === true };
  });
}
export function compareReplayReports(left, right) {
  const before = new Map(left.map(row => [row.id, row])); const after = new Map(right.map(row => [row.id, row]));
  const entries = [...new Set([...before.keys(), ...after.keys()])].sort((a, b) => a - b).map(id => {
    const a = before.get(id), b = after.get(id);
    if (!a || !b) return { id, presence: a ? "removed" : "added", decisionChanged: null, coverageChanged: null, identityChanged: null, providerEvidence: "unavailable" };
    let providerEvidence = "unavailable";
    if (a.evidence && b.evidence) {
      if (!a.complete || !b.complete) providerEvidence = "not_comparable_coverage";
      else if (!a.version || !b.version || !a.plan || !b.plan || !a.locale || !b.locale) providerEvidence = "not_comparable_context";
      else if (a.version !== b.version) providerEvidence = "projection_version_changed";
      else if (a.plan !== b.plan || a.locale !== b.locale) providerEvidence = "query_context_changed";
      else providerEvidence = a.evidence === b.evidence ? "unchanged" : "changed";
    }
    return { id, presence: "both", decisionChanged: a.decision !== b.decision,
      coverageChanged: a.coverage !== b.coverage || a.complete !== b.complete, identityChanged: a.selected !== b.selected, providerEvidence };
  });
  return { entries, decisions: entries.filter(e => e.decisionChanged).length, coverage: entries.filter(e => e.coverageChanged).length,
    identities: entries.filter(e => e.identityChanged).length, providerChanges: entries.filter(e => e.providerEvidence === "changed").length,
    notComparable: entries.filter(e => !["changed", "unchanged"].includes(e.providerEvidence)).length };
}
