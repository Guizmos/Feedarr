-- Migration 0047: Idempotent guard for the 0005_* naming collision group.
--
-- Problem
-- -------
-- Three migration files share the "0005_" prefix:
--   - 0005_activity_log.sql          (creates activity_log table)
--   - 0005_add_tvdb_id.sql           (adds releases.tvdb_id)
--   - 0005_source_categories_unified.sql (adds source_categories.unified_key/label)
--
-- Feedarr's MigrationsRunner tracks each migration by its FULL filename as the
-- primary key in schema_migrations, so all three are treated as distinct migrations.
-- Ordering is deterministic (StringComparer.OrdinalIgnoreCase alphabetical):
--   0005_activity_log < 0005_add_tvdb_id < 0005_source_categories_unified
--
-- The three migrations are independent (no inter-dependency), so current alphabetical
-- ordering is correct for correctness. However:
--   1. The naming convention makes future maintenance harder.
--   2. A filesystem with different locale could (in theory) sort differently.
--   3. The activity_log-related index collision is addressed in migration 0046.
--
-- This guard ensures all structures from the 0005_* group exist, making the system
-- resilient to any edge case where one file was skipped or applied out of order.
-- Uses ALTER TABLE ... ADD COLUMN IF NOT EXISTS (requires SQLite 3.38.0+;
-- Microsoft.Data.Sqlite 8.x bundles SQLite 3.44+).
--
-- Idempotency: all statements are safe to run on a fully-migrated database.

-- From 0005_add_tvdb_id.sql: ensure releases.tvdb_id exists.
ALTER TABLE releases ADD COLUMN IF NOT EXISTS tvdb_id INTEGER;
CREATE INDEX IF NOT EXISTS idx_releases_tvdb_id ON releases(tvdb_id) WHERE tvdb_id IS NOT NULL;

-- From 0005_source_categories_unified.sql: ensure columns exist.
ALTER TABLE source_categories ADD COLUMN IF NOT EXISTS unified_key TEXT;
ALTER TABLE source_categories ADD COLUMN IF NOT EXISTS unified_label TEXT;

-- Note: activity_log table (from 0005_activity_log.sql) and its index
-- idx_activity_created are handled by migration 0046_cleanup_orphaned_activity_logs.sql.
