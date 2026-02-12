-- Additional performance indexes for high-traffic aggregate and dashboard queries.
CREATE INDEX IF NOT EXISTS idx_releases_created_at
  ON releases(created_at_ts DESC);

CREATE INDEX IF NOT EXISTS idx_releases_category_not_null
  ON releases(category_id)
  WHERE category_id IS NOT NULL;

CREATE INDEX IF NOT EXISTS idx_releases_grabs_not_null
  ON releases(grabs DESC)
  WHERE grabs IS NOT NULL AND grabs > 0;

CREATE INDEX IF NOT EXISTS idx_releases_source_category
  ON releases(source_id, category_id);
