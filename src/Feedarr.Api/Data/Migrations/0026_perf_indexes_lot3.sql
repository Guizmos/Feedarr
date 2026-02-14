-- Performance indexes for poster queries, entity lookups and stats aggregations.

-- Accelerate poster-missing queries that JOIN on entity_id
CREATE INDEX IF NOT EXISTS idx_releases_entity_poster
  ON releases(entity_id, created_at_ts DESC)
  WHERE poster_file IS NULL;

-- Accelerate media_entities poster lookups
CREATE INDEX IF NOT EXISTS idx_media_entities_poster
  ON media_entities(poster_file)
  WHERE poster_file IS NOT NULL;

-- Accelerate stats queries grouped by unified_category
CREATE INDEX IF NOT EXISTS idx_releases_unified_category_created
  ON releases(unified_category, created_at_ts DESC)
  WHERE unified_category IS NOT NULL;

-- Accelerate external ID lookups (tvdb/tmdb)
CREATE INDEX IF NOT EXISTS idx_releases_tvdb_id
  ON releases(tvdb_id)
  WHERE tvdb_id IS NOT NULL AND tvdb_id > 0;

CREATE INDEX IF NOT EXISTS idx_releases_tmdb_id
  ON releases(tmdb_id)
  WHERE tmdb_id IS NOT NULL AND tmdb_id > 0;
