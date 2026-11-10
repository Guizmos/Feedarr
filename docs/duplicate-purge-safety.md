# Conservative duplicate purge (P1-02)

## Root cause and identity contract

The previous maintenance query partitioned releases by source_id and title_clean.
TitleParser intentionally removes episode/season tokens: S01E01 and S01E02 can
share a cleaned title. That presentation/matching identity is not a torrent identity.
The top-feed dedupe_key is similarly title-based and must not authorize deletion.

Only the following are now eligible for destructive deduplication:

- The same source_id, the same nonblank original title (exact comparison), and the
  same info_hash after ASCII-space trimming and lowercase normalization.
- A 40-character hexadecimal BitTorrent v1 hash; uniform repeated-character values
  are treated as generic placeholders and excluded. Base32, v2 hashes and magnets
  without a stored valid info_hash are intentionally not used as fallback identities.
- No contradiction in year, season, episode, resolution or release group, including
  missing-versus-known values for those fields.
- Every nonempty persisted field of the deleted row must already exist identically
  on the keeper. This includes size, codec, entity_id, seen state, manual overrides,
  external metadata, raw_json, posters and newly added schema fields by default.
- Any nonempty download/link alias must have a nonempty surviving counterpart.
  Magnet links must match exactly if either counterpart is a magnet.
- Arr status and poster-store references on a candidate must already be represented
  on the keeper, without a conflicting value.

GUID is not a fallback proof: RSS parsing may substitute a link/title, and the schema
already enforces UNIQUE(source_id,guid). No provider-specific duplicate identity with
stronger guarantees was found. URLs may expire; entity IDs identify works rather than
individual releases. None of these, nor title_clean, authorize deletion alone.

Different raw metadata, titles, editions, qualities, status snapshots or incomplete
identity can therefore leave real duplicates untouched. This is intentional. Potential
duplicates are not added to the existing API counter: groupsFound/duplicatesCount
represent only candidates that satisfy the complete purge rule at detection time.
The infohash is the source's announced torrent identity; no torrent payload is fetched
or cryptographically rehashed by maintenance.

## Selection, transaction and references

One canonical keeper per source/hash/original-title group is ranked by the number of
nonempty persisted fields, then Arr-status presence, publication/creation time and ID
(descending). Candidates with information the keeper cannot preserve are retained;
there is no speculative metadata merge. Remaining conflicts may leave multiple rows.

DetectDuplicates and PurgeDuplicates use the same candidate-query builder. Detection
uses a read transaction for a consistent snapshot. Purge starts an IMMEDIATE SQLite
transaction before re-evaluating all identities and references, then intersects that
result with the requested IDs. A stale ID, arbitrary ID or current keeper cannot be
deleted merely because it appeared in an earlier response. SQLite serializes RSS,
retention and other writers during this transaction; no new application-global lock
is introduced. Deletes are batched within one transaction, including Arr status and
poster refs. Any failure rolls back every batch and association deletion.

No physical poster, media entity or poster-match entry is removed. A candidate's
poster/reference is either absent or still represented by its keeper. Existing
poster-ref lifecycle triggers continue to apply. Retention/GC policies are unchanged.
The endpoint still defaults to purge=false and retains its DTO/signature. Its existing
frontend confirmation now describes information preservation and possible revalidation
rather than promising that the newest row is always retained.

## Verification

All destructive tests use isolated temporary SQLite databases. The episode regression
was first executed against the old implementation and failed: S01E01/S01E02 had the
same parsed title and duplicate ID 1 was incorrectly returned. It passes after the fix.
Tests also cover variants, distinct hashes/sources, invalid identity, normalization,
three occurrences, metadata/Arr/poster/entity preservation, rollback, idempotence,
stale IDs, another connection writing identity, API dry-run/default and explicit purge.

Release backend build: 0 warnings/errors. Targeted maintenance/parser/integrity tests:
85 passed. Complete backend suite: 1,002 passed, 0 failed/skipped. No user database was
opened and no purge was run against a real catalog.

The reproducible CatalogQueryPlanAndTiming_AreRecorded_OnTemporarySqlite test creates
20,000 valid-hash distinct releases. One targeted run measured 59 ms for full detection;
the previous unsafe title-based window query alone took 12 ms and returned 19,999 false
candidates. This is a local synthetic measurement, not a production throughput claim.
EXPLAIN QUERY PLAN showed a source index scan, a temporary window/order sort and primary
key/index lookups for releases, Arr status and poster refs. Complexity remains a catalog
scan plus sorting; .NET receives candidate ID pairs rather than full metadata payloads.
No migration/index is added without evidence that its maintenance cost is warranted.
