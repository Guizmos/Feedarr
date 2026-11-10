# Configuration restore V2 — lot 2

## Boundaries

The internal service uses the lot 1 archive engine. No React, wizard, upload or
new HTTP routes are added. Only configuration/control-plane tables are written.
No releases, entities, poster references, media files, GC or sync business engine
is modified by the application transaction. The ordinary RSS worker delegates a
restoration's first synchronization to the existing orchestration service.

Services exposed for lot 3: PrepareAsync, GetPendingPreview, ConfirmAsync,
CancelAsync, ConfirmRetentionAsync, ConfirmInitialSyncAsync, CancelInitialSyncAsync.
Their caller must carry the middleware's authentication marker and the existing
anti-forgery header or an accepted origin. Before setup completion, a direct
loopback request, validated bootstrap secret or middleware-consumed bootstrap
token is additionally required. Knowing the archive passphrase never authorizes
an import. Lot 3 must add narrow authorized routes; it must not bypass middleware.

## Preparation and confirmation

Preparation is serialized with legacy backup/restore operations using the existing
coordinator. The active configuration is read in one SQLite transaction and is
never changed during preparation. Matching is by normalized endpoint and provider
or application type, not display name or imported row ID. URL path/query identity
is retained. Multiple candidates or duplicate incoming source identities fail
closed; changed endpoints receive new rows/IDs, with old rows retained disabled.
Ambiguous external instances require resolution rather than merging credentials.

Only a safe preview is returned. The plan is re-protected with the destination
Data Protection purpose, and integration secrets are individually protected with
the destination API-key protector. No passphrase is persisted. The plan file is
`<data>/configuration-restore/plan.bin`, with atomic replacement, flushed content,
parent-directory synchronization and private permissions. Symlink control paths
are refused. Temporary files contain ciphertext only. A configuration fingerprint
rejects stale confirmation/application after destination configuration changes.

Confirmation is explicit and requires a separate consent flag for security changes.
Confirmed plans block new source/Arr sync leases until restart or cancellation.
Preparation/confirmation do not fetch provider APIs or weaken destination SSRF
policy. Structural/literal destination checks run before staging and again before
application; existing network transports retain their DNS/connect-time checks.

## Startup transaction and replay

The process-level data lease remains unchanged. A filesystem-only conflict check
runs before legacy restoration can swap SQLite/WAL or posters. V2 application
runs after schema migrations/Data Protection initialization, before credential
normalization can mutate the fingerprint, and before hosted workers or HTTP start.

All row mappings, settings, categories, selected categories, external instances,
legacy compatibility projections, replay marker and first-sync request are committed
through one SQLite connection/transaction, with foreign keys enabled and FULL
synchronous durability. Destination credential decryptability is checked again.

- Before commit: an error/interrupt rolls everything back; the confirmed plan stays.
- After commit: the operation ID in configuration_restore_operations prevents
  reapplication even if the process died before deleting the plan file.
- After replay/finalization: setup/security caches and bootstrap tokens are invalidated.
- Failed/corrupt/key-unreadable plans stop application; no partial import is accepted.

Historical sources/providers/applications are retained disabled, never deleted.
Matched source/application IDs remain stable. No imported SQLite row ID is reused.
Existing category definitions needed by the library are retained; incoming mappings
and selections replace configuration for matched/new sources only. External
instance precedence is preserved through local technical timestamps, and legacy
external settings are projected from those canonical instances without duplicates.

A newly appearing library/media collection after preparation invalidates the plan.
An incomplete initial configuration keeps onboarding incomplete. An existing
completed setup is not reopened as an unauthenticated setup merely because imported
integration credentials are missing. Security policy changes still require consent.

## Retention and first synchronization

Application never runs retention or purges data. More restrictive limits are stored
as pending control-plane values; prior limits stay active until ConfirmRetentionAsync.
That confirmation changes settings only and itself performs no synchronization or purge.
Unsupported retention bounds are rejected rather than relying on implicit sync-time
clamping, which could otherwise hide a reduction of the effective limits.
A newer applied restore retires obsolete pending retention decisions.

A persistent first-sync request is recorded in the same configuration transaction.
For a complete installation without existing releases/entities/poster references
or media, it runs automatically. Existing or incomplete libraries use `held`:
automatic RSS synchronization waits for explicit ConfirmInitialSyncAsync (and any
pending retention confirmation). This is necessary because the unchanged sync
engine performs retention. The admin may instead cancel the first-sync request.
A held request is an intentional safety decision, not a failed import.

The RSS worker handles the request before its ordinary pass, skipping that pass
for the cycle even when the first sync just completed. A per-service gate and atomic
SQLite claim prevent duplicate claims; successful source IDs are durably recorded.
Cancellation/interruption retries only remaining sources. Backoff starts at 60 seconds
and is bounded at one hour, in addition to normal scheduler pacing. No sensitive
provider error is stored in the job. Network work can repeat after a crash between
request completion and progress persistence; existing source/guid upserts provide
idempotent ingestion, not an impossible exactly-once external HTTP guarantee.

## Coexistence and tests

Legacy endpoints/archives/application semantics remain in place. The only changes
to PendingRestoreStore are a V2 staging conflict guard, sharing its existing
directory-flush helper, and checking the V1 preparation deadline before publication. Preparing V2 refuses legacy pending/incomplete staging;
preparing V1 refuses a pending V2 plan. A conflict at boot stops before either applies.
Restore plan files live outside poster/backup cleanup roots.

Tests use temporary SQLite/media and distinct locally protected master keys. They
cover remapping, library preservation, rollback/crash replay, stale plans, ambiguous
identities, unreadable credentials, bootstrap/CSRF, security consent, retention holds,
first-sync claims/progress/backoff/cancellation and V1/V2 exclusion.

Remaining lot 3: HTTP/UI upload and preview/confirmation, wizard branch, presentation
of held sync/pending retention and credential re-entry, and deployment-specific
integration/visual checks. No production volume or real provider is contacted here.

## V1 timeout regression coverage

Timer callback dispatch can lag behind expiration under thread-pool load. V1 now
checks a monotonic preparation deadline as well as the cancellation token before
copying and at the last cancellable publication boundary. Its public restore API
and production timeout are unchanged. Tests advance a controlled clock to the exact
deadline, independently delivering or withholding the timer callback; they also
cover late cancellation before publication. They keep all failed-state, busy-state,
missing-pending and staging-cleanup assertions, without sleeps or larger timeouts.
