-- Migration 0053: keep poster_store_refs in sync with releases lifecycle.
-- Fixes stale refs that block orphan store-dir purge.

CREATE INDEX IF NOT EXISTS idx_poster_store_refs_release_id
ON poster_store_refs(release_id);

-- Drop refs pointing to deleted releases.
DELETE FROM poster_store_refs
WHERE release_id NOT IN (SELECT id FROM releases);

-- Keep only the currently declared store_dir per release.
DELETE FROM poster_store_refs
WHERE EXISTS (
    SELECT 1
    FROM releases r
    WHERE r.id = poster_store_refs.release_id
      AND (
          r.poster_store_dir IS NULL
          OR r.poster_store_dir = ''
          OR r.poster_store_dir <> poster_store_refs.store_dir
      )
);

-- Backfill missing refs for releases that already have a store_dir.
INSERT OR IGNORE INTO poster_store_refs (store_dir, release_id, created_at_ts)
SELECT
    r.poster_store_dir,
    r.id,
    COALESCE(r.poster_updated_at_ts, r.created_at_ts, CAST(strftime('%s', 'now') AS INTEGER))
FROM releases r
WHERE r.poster_store_dir IS NOT NULL
  AND r.poster_store_dir <> '';

-- Keep refs aligned when a release is created with a poster_store_dir.
CREATE TRIGGER IF NOT EXISTS trg_poster_store_refs_after_releases_insert
AFTER INSERT ON releases
WHEN NEW.poster_store_dir IS NOT NULL AND NEW.poster_store_dir <> ''
BEGIN
    INSERT OR IGNORE INTO poster_store_refs (store_dir, release_id, created_at_ts)
    VALUES (
        NEW.poster_store_dir,
        NEW.id,
        COALESCE(NEW.poster_updated_at_ts, NEW.created_at_ts, CAST(strftime('%s', 'now') AS INTEGER))
    );
END;

-- Keep refs aligned when poster_store_dir / poster_updated_at_ts changes.
CREATE TRIGGER IF NOT EXISTS trg_poster_store_refs_after_releases_update
AFTER UPDATE OF poster_store_dir, poster_updated_at_ts ON releases
BEGIN
    DELETE FROM poster_store_refs
    WHERE release_id = NEW.id
      AND (
          NEW.poster_store_dir IS NULL
          OR NEW.poster_store_dir = ''
          OR store_dir <> NEW.poster_store_dir
      );

    INSERT OR IGNORE INTO poster_store_refs (store_dir, release_id, created_at_ts)
    SELECT
        NEW.poster_store_dir,
        NEW.id,
        COALESCE(NEW.poster_updated_at_ts, CAST(strftime('%s', 'now') AS INTEGER))
    WHERE NEW.poster_store_dir IS NOT NULL
      AND NEW.poster_store_dir <> '';
END;

-- Delete refs as soon as releases are deleted.
CREATE TRIGGER IF NOT EXISTS trg_poster_store_refs_after_releases_delete
AFTER DELETE ON releases
BEGIN
    DELETE FROM poster_store_refs
    WHERE release_id = OLD.id;
END;
