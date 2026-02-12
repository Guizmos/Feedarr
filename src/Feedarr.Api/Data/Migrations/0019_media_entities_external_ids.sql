PRAGMA foreign_keys=ON;

ALTER TABLE media_entities ADD COLUMN tmdb_id INTEGER;
ALTER TABLE media_entities ADD COLUMN tvdb_id INTEGER;
ALTER TABLE media_entities ADD COLUMN imdb_id TEXT;
ALTER TABLE media_entities ADD COLUMN ext_provider TEXT;
ALTER TABLE media_entities ADD COLUMN ext_provider_id TEXT;
ALTER TABLE media_entities ADD COLUMN ext_title TEXT;
ALTER TABLE media_entities ADD COLUMN ext_overview TEXT;
ALTER TABLE media_entities ADD COLUMN ext_tagline TEXT;
ALTER TABLE media_entities ADD COLUMN ext_genres TEXT;
ALTER TABLE media_entities ADD COLUMN ext_release_date TEXT;
ALTER TABLE media_entities ADD COLUMN ext_runtime_minutes INTEGER;
ALTER TABLE media_entities ADD COLUMN ext_rating REAL;
ALTER TABLE media_entities ADD COLUMN ext_votes INTEGER;
ALTER TABLE media_entities ADD COLUMN ext_updated_at_ts INTEGER;
ALTER TABLE media_entities ADD COLUMN ext_directors TEXT;
ALTER TABLE media_entities ADD COLUMN ext_writers TEXT;
ALTER TABLE media_entities ADD COLUMN ext_cast TEXT;

CREATE INDEX IF NOT EXISTS idx_media_entities_tmdb_id
ON media_entities(tmdb_id)
WHERE tmdb_id IS NOT NULL;

CREATE INDEX IF NOT EXISTS idx_media_entities_tvdb_id
ON media_entities(tvdb_id)
WHERE tvdb_id IS NOT NULL;

CREATE INDEX IF NOT EXISTS idx_media_entities_ext_provider
ON media_entities(ext_provider, ext_provider_id)
WHERE ext_provider IS NOT NULL AND ext_provider_id IS NOT NULL;
