PRAGMA foreign_keys=ON;

ALTER TABLE releases ADD COLUMN title_clean TEXT;
ALTER TABLE releases ADD COLUMN year INTEGER;
ALTER TABLE releases ADD COLUMN season INTEGER;
ALTER TABLE releases ADD COLUMN episode INTEGER;
ALTER TABLE releases ADD COLUMN resolution TEXT;
ALTER TABLE releases ADD COLUMN source TEXT;
ALTER TABLE releases ADD COLUMN codec TEXT;
ALTER TABLE releases ADD COLUMN release_group TEXT;
ALTER TABLE releases ADD COLUMN media_type TEXT;

ALTER TABLE releases ADD COLUMN tmdb_id INTEGER;
ALTER TABLE releases ADD COLUMN poster_path TEXT;
ALTER TABLE releases ADD COLUMN poster_file TEXT;
ALTER TABLE releases ADD COLUMN poster_updated_at_ts INTEGER;

CREATE INDEX IF NOT EXISTS idx_releases_tmdb_id ON releases(tmdb_id);
CREATE INDEX IF NOT EXISTS idx_releases_media_type ON releases(media_type);
