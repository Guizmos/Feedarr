PRAGMA foreign_keys=ON;

ALTER TABLE media_entities ADD COLUMN request_tmdb_id INTEGER;
ALTER TABLE media_entities ADD COLUMN request_tmdb_status TEXT;
ALTER TABLE media_entities ADD COLUMN request_tmdb_updated_at_ts INTEGER;

CREATE INDEX IF NOT EXISTS idx_media_entities_request_tmdb_id
ON media_entities(request_tmdb_id)
WHERE request_tmdb_id IS NOT NULL;

CREATE INDEX IF NOT EXISTS idx_media_entities_request_tmdb_status
ON media_entities(request_tmdb_status)
WHERE request_tmdb_status IS NOT NULL AND request_tmdb_status <> '';
