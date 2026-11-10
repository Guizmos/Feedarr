# Configuration backup V2 — lot 1

## Scope and internal API

`ConfigurationArchiveService` is registered in DI. `ExportAsync(passphrase, ct)`
returns an encrypted ZIP in memory. `ReadAsync(stream, passphrase, ct)` reads V2
or a compatible legacy ZIP and returns a sensitive in-memory document plus a safe
aggregate preview and logical credential fields requiring re-entry.

No HTTP endpoints, upload flow, restore application, synchronization, wizard or
React changes are included. Existing Backup V1 and PendingRestoreStore are unchanged.
The unrelated poster diagnostic script is not part of this change.

Never log or return `result.Configuration` from an endpoint. It contains plaintext
credentials in memory (including the historical `api_key_encrypted` application
field name). The property is excluded from serialization of the read result.
The passphrase is never stored. Byte buffers holding plaintext/key material are
cleared where owned by this service; immutable strings in the document remain
caller-owned managed data. Lot 2 must protect credentials with the destination
`IApiKeyProtectionService` before persisting anything.

## Wire format

Exactly two unique root entries:

- `manifest.json`: format `Feedarr.Configuration`, formatVersion 2,
  schemaVersion 1, KDF/cipher names, iteration count, base64 salt and nonce.
- `configuration.enc`: ciphertext followed by the 16-byte GCM authentication tag.

AES-256-GCM uses the **exact manifest entry bytes** as associated authenticated
data. Salt is 16 random bytes; nonce is 12 fresh random bytes. PBKDF2-HMAC-SHA256
uses 600,000 iterations and derives 32 bytes. Readers accept only 600,000 through
1,200,000 iterations, the exact supported algorithm names and sizes. Unsupported
future wire/schema versions fail closed. A wrong passphrase and failed GCM
integrity have the same safe error. Passphrases must contain 12–1024 characters;
no Unicode normalization is applied. Use TLS when future endpoints are introduced.

Calibration on the local ARM64 development machine: 3 isolated PBKDF2 runs,
median 89.8 ms (88.2–90.5 ms). This is not a NAS benchmark or an import throughput
promise. The work factor is fixed for writers and encoded in the manifest.

Limits: V2 ZIP 16 MiB; plaintext configuration 8 MiB; manifest 4 KiB;
10,000 configuration rows total; individual strings 16,384 characters; JSON depth
32. ZIP reads enforce both declared and actual expanded sizes. Duplicate entries,
duplicate JSON properties, unknown model/manifest fields, missing/null sections,
invalid types, duplicate logical keys and dangling references are rejected.
The SQLite projection also bounds cumulative input text before model allocation.
ZIP entry counts and a 64 KiB central-directory bound are checked before entry-list
allocation; ZIP64, multi-disk ZIPs and ZIP comments are not supported.

## Configuration schema 1

The model carries five settings sections (`general`, `ui`, `arr`, `maintenance`,
`security`) and explicit allowlisted lists of providers, sources, external provider
instances, Arr/request applications, needed category definitions, mappings and selections.
Category definitions are restricted to selected/mapped IDs and their ancestors;
the complete discovered CAPS cache is not exported.

- Row identities are fresh `ref-<uuid>` logical keys, never source SQLite IDs.
- Source/provider and category/source links reference those keys.
- UI default source/application filters are remapped to logical keys; stale deleted
  targets are reset to the empty filter. External category/profile/tag IDs remain
  remote integration identifiers.
- External instance `priority` preserves their effective ordering without carrying
  operational timestamps. Lower values have precedence within a provider. Lot 2
  must reproduce this precedence when creating destination instances.
- Existing external instances are authoritative per provider. Old individual
  settings and the legacy `external` object are converted only where that provider
  has no instance; dedicated legacy keys take precedence over the old object.
- `onboardingDone` is excluded: completion must be computed by the restore flow.
- Security hash/salt are included inside the encrypted payload, not a plaintext
  password. The current Basic credential representation is preserved.
- Settings use the existing typed settings contracts; row columns are listed in
  ConfigurationSchema. Empty credentials are reported as incomplete, never filled
  with local ciphertext. An unreadable protected credential aborts a V2 export,
  including credentials of disabled instances.

The preview counts configured sources as indexers, distinguishes indexer providers
from external metadata providers, and counts applications/categories/settings.
`IsComplete` means sources and the required enabled-integration credentials are
present. It does not certify remote reachability, destination SSRF authorization,
bootstrap security or wizard completion. Those checks belong to later lots.

Excluded: releases, entities, metadata, posters, thumbnails, matching/cache data,
statistics, sync history, migration records, key rings/master keys, restore state,
Docker/environment settings and browser-local preferences.

## Legacy reader

Existing BackupValidationService validates V1 metadata/version/hash requirements.
The DB entry is size-bounded, hash-verified, then opened through the existing
SQLitePCLRaw `sqlite3_deserialize` in a private read-only in-memory database.
No SQLite snapshot is extracted to disk, no migration is executed, and the active
database is not opened by a legacy read. Only configuration tables are selected;
views/virtual tables are refused. Native SQLite value/schema limits, cancellation
and a 15-second VM execution budget constrain hostile databases.

The archive and legacy DB each have a 64 MiB cap. Larger legacy backups require a
future bounded conversion on the original installation; they are explicitly rejected,
not silently truncated. A standalone snapshot's WAL header flags are normalized only
in its private memory image, after verifying the original checksum. No external WAL
or SHM is consumed. `config.json` is redacted and never treated as authoritative.

The old checksum is not cryptographic proof of who produced the archive. All
configuration/model checks still apply, and no recovered configuration is applied.
Older provider `api_key` columns, absent newer configuration tables, legacy JSON
settings and pre-migration string settings are supported. Unknown incompatible
structures are rejected rather than guessing.

Secrets decryptable with the available original key ring are recovered in memory.
Without it, protected values are empty and the result explicitly lists logical item
keys and credential fields to re-enter, including optional/disabled unreadable
credentials. A complete cross-installation recovery from V1 cannot be promised
without the original key ring. Master keys are never imported/exported.

## Reserved for lots 2 and 3

Lot 2: preview/confirmation plan, source identity reconciliation while preserving
library rows, destination secret protection, atomic application/recovery, setup and
security cache invalidation, compatibility with pending V1 restores, retention
policy decisions and durable first-sync scheduling.

Lot 3: authorized initial-setup/import routes, file upload/download UX, passphrase
entry, wizard branch, settings UI and integration tests. Import authorization must
not rely on knowing the archive passphrase. Destination URL/SSRF policies must be
reapplied before any restored integration is contacted.
