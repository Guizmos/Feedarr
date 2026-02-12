PRAGMA foreign_keys=ON;

ALTER TABLE releases ADD COLUMN tvdb_id INTEGER;

CREATE INDEX IF NOT EXISTS idx_releases_tvdb_id ON releases(tvdb_id);
