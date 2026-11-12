import test from "node:test";
import assert from "node:assert/strict";
import { parseReplayIds, replayPayload, replayCategories } from "../matchingReplayModel.js";
import { parseReplayReport, compareReplayReports, replayReportMaxBytes } from "../matchingReplayComparison.js";

const selection = overrides => ({ method: "ids", categories: ["films"], releaseIds: [7, 2], filter: "all", fairBudget: true, ...overrides });
const row = (id = 1, overrides = {}) => ({ detail: { releaseId: id, decision: "accept", selectedIdentity: { provider: "tmdb", type: "movie", id: "100" } },
  coverage: "complete", providerEvidenceFingerprint: "a".repeat(64), providerEvidenceComplete: true, queryPlanFingerprint: "b".repeat(64), searchLocale: "fr-FR", providerEvidenceVersion: 1, ...overrides });
const report = rows => parseReplayReport(JSON.stringify({ entries: rows }));
const compare = (left, right) => compareReplayReports(report(left), report(right));

test("IDs preserve order and accept ordinary separators only", () => {
  assert.deepEqual(parseReplayIds("7, 2; 9\n10"), [7, 2, 9, 10]); assert.deepEqual(parseReplayIds(""), []);
  for (const value of ["0", "-1", "01", "+1", "1e3", "0x10", "1.5", "1,1", "NaN", "9007199254740992", "1 ".repeat(201), "A".repeat(4001)]) assert.throws(() => parseReplayIds(value));
});
test("selection and preview IDs are copied and bounded", () => {
  const selected = selection(); const expected = [7, 2]; const payload = replayPayload(10, "tmdb", 6, 1, selected, expected);
  selected.releaseIds[0] = 999; expected[0] = 999;
  assert.deepEqual(payload.selection.releaseIds, [7, 2]); assert.deepEqual(payload.expectedIds, [7, 2]);
  for (const invalid of [selection({ categories: [] }), selection({ categories: ["games"] }), selection({ categories: ["films", "films"] }), selection({ releaseIds: [] }), selection({ releaseIds: [1, 1] }), selection({ filter: "SQL" }), selection({ method: "sample" })]) assert.throws(() => replayPayload(10, "offline", 30, 0, invalid));
  assert.equal(replayCategories.length, 5);
});
test("decision change does not imply provider response change", () => {
  const result = compare([row()], [row(1, { detail: { releaseId: 1, decision: "reject", selectedIdentity: null } })]);
  assert.equal(result.decisions, 1); assert.equal(result.coverage, 0); assert.equal(result.identities, 1);
  assert.equal(result.providerChanges, 0); assert.equal(result.entries[0].providerEvidence, "unchanged");
});
test("provider projection variations are separate from decisions and coverage", () => {
  const result = compare([row()], [row(1, { providerEvidenceFingerprint: "c".repeat(64) })]);
  assert.equal(result.decisions, 0); assert.equal(result.coverage, 0); assert.equal(result.providerChanges, 1);
});
test("partial coverage cannot claim a complete provider response variation", () => {
  const result = compare([row()], [row(1, { coverage: "http_budget_exhausted", providerEvidenceComplete: false, providerEvidenceFingerprint: "c".repeat(64) })]);
  assert.equal(result.coverage, 1); assert.equal(result.providerChanges, 0); assert.equal(result.entries[0].providerEvidence, "not_comparable_coverage");
});
test("query locale and evidence version changes are explicitly non comparable", () => {
  for (const [fields, expected] of [[{ queryPlanFingerprint: "d".repeat(64) }, "query_context_changed"], [{ searchLocale: "en-US" }, "query_context_changed"], [{ providerEvidenceVersion: 2 }, "projection_version_changed"], [{ searchLocale: null }, "not_comparable_context"]]) {
    const result = compare([row()], [row(1, fields)]); assert.equal(result.providerChanges, 0); assert.equal(result.entries[0].providerEvidence, expected);
  }
});
test("legacy reports remain readable without fabricating provider evidence", () => {
  const old = { detail: { releaseId: 1, decision: "not_evaluable" }, coverage: "offline_provider_responses_unavailable" };
  const result = compare([old], [row()]); assert.equal(result.decisions, 1); assert.equal(result.entries[0].providerEvidence, "unavailable");
});
test("added and removed IDs are distinct from changed decisions", () => {
  const result = compare([row(1), row(2)], [row(2), row(3)]);
  assert.deepEqual(result.entries.map(e => e.presence), ["removed", "both", "added"]); assert.equal(result.decisions, 0);
});
test("untrusted report titles responses and secrets are discarded before display", () => {
  const hostile = row(1, { detail: { releaseId: 1, decision: "reject", rawTitle: "https://private/?apikey=CANARY", candidates: [{ title: "<script>CANARY</script>" }] }, rawResponse: "CANARY" });
  const safe = report([hostile]); const output = JSON.stringify(compareReplayReports(safe, safe));
  assert.ok(!output.includes("CANARY")); assert.ok(!output.includes("<script>")); assert.ok(!JSON.stringify(safe).includes("https://"));
});
test("malformed oversized duplicate and unsafe comparison inputs are rejected", () => {
  for (const text of ["not json", "{}", " ".repeat(replayReportMaxBytes + 1), JSON.stringify({ entries: [row(), row()] }), JSON.stringify({ entries: Array.from({ length: 201 }, (_, i) => row(i + 1)) }), JSON.stringify({ entries: [row(-1)] }), JSON.stringify({ entries: [row(1, { coverage: "https://secret" })] }), JSON.stringify({ entries: [row(1, { providerEvidenceFingerprint: "secret" })] }), JSON.stringify({ entries: [row(1, { searchLocale: "https://private" })] })]) assert.throws(() => parseReplayReport(text));
});
test("PascalCase backend JSON is supported as well as web camelCase", () => {
  const parsed = parseReplayReport(JSON.stringify({ Entries: [{ Detail: { ReleaseId: 4, Decision: "reject" }, Coverage: "complete" }] }));
  assert.equal(parsed[0].id, 4); assert.equal(parsed[0].evidence, null);
});
