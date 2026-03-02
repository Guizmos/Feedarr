-- Migration 0045: Rename providers.api_key → providers.api_key_encrypted.
--
-- Context: The providers table stored its API key in a column named "api_key",
-- which was misleading because the ProviderRepository already encrypts the value
-- (ENC: prefix) before writing, and decrypts it on read.
-- arr_applications uses the clearer name "api_key_encrypted"; this migration
-- brings providers in line with that convention.
--
-- Data safety:
--   - RENAME COLUMN is atomic in SQLite (no data copy, no data loss).
--   - Values already encrypted by ApiKeyMigrationService keep their ENC: prefix.
--   - Values still in plaintext (pre-migration startup) will be encrypted by
--     ApiKeyMigrationService.MigrateAsync() which runs immediately after migrations.
--   - Indexes (idx_providers_type, idx_providers_base_url, idx_providers_enabled)
--     do not reference api_key, so they are unaffected.
--
-- Requires SQLite 3.25.0+ (ALTER TABLE ... RENAME COLUMN).
-- Microsoft.Data.Sqlite 8.x bundles SQLite 3.44+.

ALTER TABLE providers RENAME COLUMN api_key TO api_key_encrypted;
