# Deferred SQLite restoration (P1-03)

## Preparation during normal operation

The existing ZIP format (SQLite database, config.json, info.json) and archive/version,
size, SHA-256 and SQLite integrity validations are retained. confirm=false remains a
preview. confirm=true now prepares restoration only: it never replaces the active DB,
checkpoints it, clears SQLite pools or sets the old hot-restore restart flag.
Credentials are normalized/re-encrypted only in the staging database with the existing
protection service. Staging connections explicitly disable pooling.

Preparation uses a GUID-owned building directory under DATA_DIR/restore-state. The
normalized database is flushed, integrity checked and hashed. Available local poster
files, canonical store files and thumbnails are copied into this private staging area;
a SHA-256 inventory covers every copied file. Symlinks are not traversed. Publishing
moves the complete directory into stages/<id>, then atomically replaces pending.json.
File flushes and POSIX directory fsync barriers precede publication. The coordinator
still serializes backup operations but preparation does not drain or block RSS sync.
After preparation, normal sync, metadata, poster and backup operations continue.

Exactly one pending operation is referenced. Replacement requires the currently
pending ID, explicitly confirmed by the UI. The identity is checked under a filesystem
operation lock at publication. Invalid/cancelled preparation cannot replace the previous
pointer. Cancellation checks the same identity, removes only its pointer/stage, and
never opens or changes the active DB. No pending restore has a TTL. Backup ZIP purge
and poster GC operate on other directories and cannot remove the pinned copy.

## Associated files and compatibility

Current Feedarr backups do NOT contain posters or thumbnails. This change does not
invent a different archive format or pretend it can recover absent files. It pins the
available local poster tree at preparation time, independently of later GC or edits.
References already unavailable or unsafe are removed only from the staged DB, including
release/entity poster references and stale poster-match entries. Missing store dirs are
also invalidated. Metadata and releases themselves remain. The API/banner report the
number of removed references, so this limitation is visible before any restart and the
user may cancel. config.json remains an informational export; actual settings come
from SQLite. The live key ring, application configuration and master key are retained.

## Startup sequence and crash recovery

Program resolves the data directory, acquires a process-lifetime exclusive lease for
that volume and calls ApplyAtStartup BEFORE DataProtection setup, service-provider
construction, active Db/repository initialization, migrations, workers or HTTP.
Only the startup applier's dedicated, unpooled SQLite connections are used during
application. A second Feedarr instance participating in this lease cannot start on the
same data directory. Operators must still stop old versions/external SQLite tools.

1. Read pending.json, or recover operation.json if a previous startup was interrupted.
   Validate required fields, GUID, versions, paths, database hash/integrity and poster
   inventory. Missing/corrupt state stops startup; it is not silently ignored.
2. Before modifying active files, use SQLite BackupDatabase to make a consistent rescue
   of the current DB, including committed WAL records. There is no checkpoint+file-copy
   fallback. Write to copying.db; verify/flush it, publish original.db, then persist the
   operation journal. An incomplete rescue copy is rebuilt from the untouched active DB.
3. With durable intent/rescue present, construct install copies from the immutable
   staging. Move current posters into the rescue directory (once), install pinned
   posters, remove obsolete active WAL/SHM and atomically publish the staged main DB.
   A file rename alone is NOT treated as a multi-file transaction.
4. Recheck installed database hash/integrity and poster inventory. Persist state=applied,
   remove pending pointer, then journal. Only then may normal startup proceed.
5. Remove the now-unreferenced stage. Rescue DB and original posters are intentionally
   retained outside ordinary ZIP purge. Completed/cancelled stage cleanup can be retried
   at startup; incomplete unreferenced building directories are also cleaned there.

Recovery is roll-forward from the immutable staging, not an implicit merge. Every
interruption after journal publication reuses the original rescue and repeats the
installation. No endpoint/worker runs between these steps. Failed validation never
falls through into normal startup. A failed/unrepairable stage or rescue requires an
operator to repair/recover offline; do not delete the journal to force boot with mixed
files. The last verified rescue is not deleted automatically. Expect extra disk space
for staging, install copies and retained rescues. Read-only/unsupported volumes or
cross-filesystem poster mounts can cause explicit startup refusal rather than unsafe
fallback. Linux/macOS use file flushes plus directory fsync; Windows uses flushed files
and atomic renames but directory-fsync/power-loss guarantees have not been validated.

The old restore.restart-required.flag is supported only for upgrades from the previous
hot-restore implementation and is cleared by the existing post-migration initializer.
New pending restores do not use that flag to block normal backup operations.

## API and UI

- POST /api/system/backups/{name}/restore: existing preview/confirm contract; preparation
  returns needsRestart=true, credential counts and missingPosters.
- Optional replacePendingId=<current id>: explicit compare-and-replace confirmation.
- GET /api/system/backups/restore-state: pending/state/id/backupName/preparedAtTs,
  missingPosters and safe error code; no filesystem paths or secret material.
- DELETE /api/system/backups/pending-restore?id=<current id>: confirmed cancellation.
- Existing GET /api/system/backups/state also includes restorePending and needsRestart.

Existing Basic/Smart middleware, CSRF handling and global RequireAuthFilter remain in
force. Filesystem errors are logged server-side; preparation/cancellation responses do
not echo internal paths. No restart is triggered automatically.

The Shell/SetupShell banner is directly below Topbar and precedes the body/subbars.
It is in normal flex flow. Content and sidebar heights use their remaining parent
space, including mobile drawers; no fixed banner offset or absolute banner is added.
Polling plus restore:changed events refresh state across navigation/reload/cancellation.
An operation ID is used for cancellation/replacement to reject stale confirmations.
The banner explains that later writes will not be merged and reports missing posters.
Settings no longer disables all backup operations merely because a restore is pending.

## Validation and remaining gate

- Release backend build: 0 warnings/errors.
- Backup/SQLite/pending restore tests: 42 passed.
- Complete backend suite: 1,029 passed (0 failures/skips).
- Frontend suite: 272 passed, including real JSX server rendering of notice states and
  confirmed cancellation/error behavior. Vite and ESLint pass without warnings.
- Fault tests cover nine persistent boundaries, invalid/missing staging, interrupted
  rescue copy, WAL recovery, filesystem failure, corrupted rescue/journal, symlinks,
  cancellation/replacement, prolonged waiting, live writes and pinned posters after GC.
- An actual isolated Feedarr process on a temporary data directory was prepared through
  HTTP, stopped and restarted. It reported applied, restored the chosen backup settings
  and discarded a different setting written after preparation. HTTP replacement without
  confirmation returned 409; explicit replacement and cancellation worked.
- No production database/container was opened, replaced or restarted. No Docker runtime
  is available here. Fault injection does not replace a real Docker/persistent-volume
  crash test or validate NAS/hardware flush guarantees.

IMPORTANT: rendered browser checks remain unverified. The browser access host was
unavailable and native Safari automation could not obtain a window. Library filters
open/closed, narrow screens and light/dark layout must still be reviewed on-screen.
Therefore the requested conditional commit/push is withheld; this is local work pending
that important validation, not a claim that P1-03 is fully closed.


## Production lifecycle correction (10 November 2026)

The updated UI opts into `POST .../restore?confirm=true&background=true`. After
filesystem preflight this returns HTTP 202 with `accepted` and `preparationId`.
`GET /api/system/backups/state` includes `preparation` (id/state/error/result).
The hosted BackupService owns the job independently of HTTP cancellation and uses
the same ID in the atomically published manifest. The UI requires a server-read
matching pending manifest before reporting preparation, with bounded polling and
explicit errors. Without `background=true`, the historical synchronous completion
contract is preserved for existing clients. The archive and startup-apply formats
are unchanged. See [the production audit](production-regressions-2026-11-10.md) for
NAS evidence, protections, validation and residual limits.
