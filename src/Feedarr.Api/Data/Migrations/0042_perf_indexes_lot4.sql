-- Performance index: full entity_id scan for stats JOIN (releases ↔ media_entities).
-- The existing partial index (idx_releases_entity_poster, migration 0026) only covers
-- rows WHERE poster_file IS NULL, so the StatsProviders aggregation query was doing a
-- full table scan when joining media_entities ON me.id = r.entity_id.
CREATE INDEX IF NOT EXISTS idx_releases_entity_id
  ON releases(entity_id)
  WHERE entity_id IS NOT NULL;
