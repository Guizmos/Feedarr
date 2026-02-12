-- Performance indexes for hot paths
CREATE INDEX IF NOT EXISTS idx_releases_source_published
  ON releases(source_id, published_at_ts DESC);

CREATE INDEX IF NOT EXISTS idx_releases_source_created
  ON releases(source_id, created_at_ts);

-- Missing posters (scoped by source_id, matching hot WHEREs)
CREATE INDEX IF NOT EXISTS idx_releases_missing_poster_source_published
  ON releases(source_id, published_at_ts DESC, id DESC)
  WHERE poster_file IS NULL OR poster_file = '';

CREATE INDEX IF NOT EXISTS idx_releases_missing_poster_source_updated
  ON releases(source_id, poster_updated_at_ts)
  WHERE poster_file IS NULL OR poster_file = '';
