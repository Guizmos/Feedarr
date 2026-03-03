-- Phase 2: Poster canonical store
-- Add poster_key and poster_store_dir to releases and media_entities.
-- Add poster_store_refs refcount table (Plan B: no hardlinks, cross-platform).

ALTER TABLE releases ADD COLUMN poster_key TEXT;
ALTER TABLE releases ADD COLUMN poster_store_dir TEXT;

ALTER TABLE media_entities ADD COLUMN poster_key TEXT;
ALTER TABLE media_entities ADD COLUMN poster_store_dir TEXT;

-- Backfill poster_key from existing poster_provider / poster_provider_id
UPDATE releases
SET poster_key = poster_provider || ':' || poster_provider_id
WHERE poster_provider IS NOT NULL AND poster_provider <> ''
  AND poster_provider_id IS NOT NULL AND poster_provider_id <> '';

-- Backfill poster_store_dir: lower(provider)-providerId
-- SQLite has no REGEXP_REPLACE; we keep the raw value (safe for path resolver validation later).
UPDATE releases
SET poster_store_dir = lower(poster_provider) || '-' || poster_provider_id
WHERE poster_provider IS NOT NULL AND poster_provider <> ''
  AND poster_provider_id IS NOT NULL AND poster_provider_id <> ''
  AND poster_store_dir IS NULL;

-- Refcount table: one row per (storeDir, releaseId) pair
CREATE TABLE IF NOT EXISTS poster_store_refs (
  store_dir      TEXT    NOT NULL,
  release_id     INTEGER NOT NULL,
  created_at_ts  INTEGER NOT NULL,
  PRIMARY KEY (store_dir, release_id)
);

CREATE INDEX IF NOT EXISTS idx_poster_store_refs_store_dir
ON poster_store_refs(store_dir);

-- Backfill refs from releases that already have a store_dir
INSERT OR IGNORE INTO poster_store_refs (store_dir, release_id, created_at_ts)
SELECT poster_store_dir, id, COALESCE(poster_updated_at_ts, created_at_ts)
FROM releases
WHERE poster_store_dir IS NOT NULL AND poster_store_dir <> '';
