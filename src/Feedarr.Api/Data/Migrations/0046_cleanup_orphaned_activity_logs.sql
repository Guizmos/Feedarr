-- Migration 0046: Drop the orphaned activity_logs table and fix the index naming collision.
--
-- Background
-- ----------
-- 0001_init.sql created table "activity_logs" (plural) with columns:
--   id, created_at_ts, level, source_id, message, details_json
-- It also created index "idx_activity_created" ON activity_logs(created_at_ts).
--
-- 0005_activity_log.sql created table "activity_log" (singular, current authoritative table)
-- with columns: id, source_id, level, event_type, message, data_json, created_at_ts
-- It tried to create "idx_activity_created" ON activity_log(created_at_ts DESC), but
-- SQLite's IF NOT EXISTS saw the name already taken (by 0001's index on activity_logs)
-- and SKIPPED the creation. So activity_log had no idx_activity_created index.
--
-- The ActivityRepository uses only "activity_log" (singular).
-- "activity_logs" (plural) is an orphaned table never written to after migration 0005.
--
-- This migration:
--   1. Migrates any rows from activity_logs → activity_log (best-effort, no-overwrite).
--   2. Drops activity_logs (also drops its idx_activity_created index automatically).
--   3. Recreates idx_activity_created on the authoritative activity_log table.
--
-- Idempotency: DROP TABLE IF EXISTS and CREATE INDEX IF NOT EXISTS make this safe to
-- re-run (though the migration runner tracks by filename and only runs it once).

PRAGMA foreign_keys=OFF;

-- Step 1: Migrate any rows that exist in the orphaned table.
-- Column mapping:
--   activity_logs.message      → activity_log.message
--   activity_logs.details_json → activity_log.data_json
--   activity_logs.level        → activity_log.level
--   activity_logs.source_id    → activity_log.source_id
--   activity_logs.created_at_ts → activity_log.created_at_ts
--   activity_log.event_type    = 'migrated' (column not present in activity_logs)
--
-- Deduplication heuristic: skip rows where (created_at_ts, source_id, message)
-- already exist in activity_log to avoid inserting duplicate entries.
INSERT INTO activity_log (source_id, level, event_type, message, data_json, created_at_ts)
SELECT
    al.source_id,
    al.level,
    'migrated',
    al.message,
    al.details_json,
    al.created_at_ts
FROM activity_logs al
WHERE NOT EXISTS (
    SELECT 1
    FROM activity_log cur
    WHERE cur.created_at_ts = al.created_at_ts
      AND cur.source_id     IS al.source_id
      AND cur.message       IS al.message
);

-- Step 2: Drop the orphaned table.
-- This also automatically drops the idx_activity_created index that was on activity_logs.
DROP TABLE IF EXISTS activity_logs;

-- Step 3: Recreate idx_activity_created on the authoritative table.
-- Previously blocked by the naming collision; now safe since activity_logs is gone.
CREATE INDEX IF NOT EXISTS idx_activity_created ON activity_log(created_at_ts DESC);

PRAGMA foreign_keys=ON;
