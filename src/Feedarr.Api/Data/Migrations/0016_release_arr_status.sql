CREATE TABLE IF NOT EXISTS release_arr_status (
  release_id INTEGER PRIMARY KEY,
  in_sonarr INTEGER NOT NULL DEFAULT 0,
  in_radarr INTEGER NOT NULL DEFAULT 0,
  sonarr_url TEXT,
  radarr_url TEXT,
  checked_at_ts INTEGER NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_release_arr_status_checked_at
  ON release_arr_status(checked_at_ts);
