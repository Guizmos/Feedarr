CREATE TABLE IF NOT EXISTS media_entity_arr_status (
  entity_id INTEGER PRIMARY KEY,
  in_sonarr INTEGER NOT NULL DEFAULT 0,
  in_radarr INTEGER NOT NULL DEFAULT 0,
  sonarr_url TEXT,
  radarr_url TEXT,
  checked_at_ts INTEGER NOT NULL,
  match_method TEXT,
  sonarr_item_id INTEGER,
  radarr_item_id INTEGER
);

CREATE INDEX IF NOT EXISTS idx_media_entity_arr_status_checked_at
  ON media_entity_arr_status(checked_at_ts);

CREATE INDEX IF NOT EXISTS idx_media_entity_arr_status_in_sonarr
  ON media_entity_arr_status(in_sonarr);

CREATE INDEX IF NOT EXISTS idx_media_entity_arr_status_in_radarr
  ON media_entity_arr_status(in_radarr);
