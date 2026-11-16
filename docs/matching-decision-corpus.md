# Bounded decision corpus

The existing replay endpoint accepts the additive flag `captureRankingInput` (default
false). Capture requires explicit `selection.method=ids`, at most 20 distinct IDs,
and retains the existing request budget (maximum 30). Capture sessions stop after
60 seconds. Export uses the existing GET replay/export endpoint; callers save its
JSON outside Git. The normal matching API keeps its previous decisions/reason body.

ShadowEntry.outcome distinguishes no_candidates, candidates_rejected, ambiguous,
coverage_incomplete and accept while preserving Decision/Reasons. Active video
persists no_candidates/candidates_rejected instead of undifferentiated reject;
public API reason remains reject. Missing coverage retains its detailed existing
reason and maps to coverage_incomplete in the diagnostic projection.

Opt-in RankingInput contains a format version, release ID, assembly informational
version, exact ranker context, enriched candidate DTOs, expected decision/identity
and successful TMDB query-page metadata. Existing Detail includes aliases, scores,
positive reasons and contradictions. Maximum 64 KiB per input (20 inputs <= 1.25 MiB,
excluding the existing report). Sensitive or oversized inputs are omitted entirely,
never redacted into a different replay decision. There are no provider response
bodies, HTTP URLs, credentials or indexer objects in the input schema.

RankingCorpus.Replay reconstructs the ranking decision without HTTP. This is a
ranker corpus, not a transport replay: failed/incomplete collections do not export
an exact ranking input; TVMaze evidence-call provenance is not captured; the full
report still uses the existing diagnostic projection. Replay collection is not
asserted equivalent to the production pipeline (notably movie query/year plans).
No new disk writer, table, telemetry service or production decision path is added.

Local preparation: /private/tmp/feedarr-decision-corpus contains an isolated SQLite
backup and an explicit 18-release manifest including Astroboy, 66 minutes, category
rejects/ambiguities and identified controls. It is selection only, not a provider
corpus. A single targeted public TVMaze probe failed with URLError; stopped without
retry. No real provider ranking inputs were obtained. Synthetic unit tests verify
roundtrip, omission of secrets/oversized input, outcome mapping and independent
concurrent exports; existing replay tests cover cancellation and ownership.
