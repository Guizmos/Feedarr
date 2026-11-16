# Provider pagination capacity

## Demonstrated cause

The active video V2 path rejected total_pages > 3 on the first page of each
query/locale, despite existing per-attempt limits of 12 search requests and 100
ranked candidates. This prevented fully collectable 61–100-result searches.
Replay has its own request/result limits and does not produce this exact reason.
No filter by hypothetical year, first-hit shortcut or narrower competitor set is safe.

## Change

Replace the independent three-page cutoff with existing capacity checks:
- reject a single search announcing more than MaximumResults candidates;
- reject when its remaining pages exceed the remaining 12-request allowance;
- otherwise collect every page, every generated distinct title and every locale;
- keep the final aggregate candidate limit, stable totals, page identity uniqueness,
  timeout, cancellation and all ranking/publication guards.

No new budget or scoring parameter. In particular this is not an unlimited paging
change. Multiple searches may still exhaust the existing global limits.

The client already caches successful pages for two minutes, keyed by credential
hash/type/title/locale/page, and coalesces concurrent identical requests. Episodes
with the same parsed title can reuse those pages. The adapter already merges
identical provider identities across locales while preserving conflicting years
and official title aliases. Duplicates inside/across pages of a search remain an
error: discarding them cannot prove that omitted competitors were collected.

## Frozen simulated corpus

Same two four-page, 80-candidate response sets before and after:
- sole exact candidate at final position: before page-budget abstention, one call;
  after typed identity 100 and successful publication, eight calls (two locales).
- another exact competitor at penultimate position: before page-budget abstention,
  one call; after business ambiguity, no identity/poster, eight calls.

Technical page-budget failures: 2 -> 0; verified synthetic publications: 0 -> 1;
incorrect synthetic publications: 0 -> 0. Candidate identities are fixture truth,
not verified real-world works. A 120-candidate control still abstains after one call.
79 targeted tests pass including existing aliases, variants, competing identities,
provider errors/timeouts and stability controls. Release build/diff check pass.

The snapshot contains 93 historical page-budget releases (71 probable works in the
previous conservative grouping), but has no provider total-page responses. The
fraction within the 61–100 range and real recoveries are unknown; some failures
will correctly become result-budget/search-budget failures. No supplier requests,
active database modifications or synchronization were performed for this change.
