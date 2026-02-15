-- Extend arr_applications type check to support Seer
CREATE TABLE IF NOT EXISTS arr_applications_new (
  id                  INTEGER PRIMARY KEY AUTOINCREMENT,
  type                TEXT NOT NULL CHECK(type IN ('sonarr', 'radarr', 'overseerr', 'jellyseerr', 'seer')),
  name                TEXT,
  base_url            TEXT NOT NULL,
  api_key_encrypted   TEXT NOT NULL,
  is_enabled          INTEGER NOT NULL DEFAULT 1,
  is_default          INTEGER NOT NULL DEFAULT 0,

  -- Default settings
  root_folder_path    TEXT,
  quality_profile_id  INTEGER,
  tags                TEXT,

  -- Sonarr-specific defaults
  series_type         TEXT,
  season_folder       INTEGER DEFAULT 1,
  monitor_mode        TEXT,
  search_missing      INTEGER DEFAULT 1,
  search_cutoff       INTEGER DEFAULT 0,

  -- Radarr-specific defaults
  minimum_availability TEXT,
  search_for_movie     INTEGER DEFAULT 1,

  created_at_ts       INTEGER NOT NULL,
  updated_at_ts       INTEGER NOT NULL
);

INSERT INTO arr_applications_new(
  id, type, name, base_url, api_key_encrypted, is_enabled, is_default,
  root_folder_path, quality_profile_id, tags,
  series_type, season_folder, monitor_mode, search_missing, search_cutoff,
  minimum_availability, search_for_movie,
  created_at_ts, updated_at_ts
)
SELECT
  id, type, name, base_url, api_key_encrypted, is_enabled, is_default,
  root_folder_path, quality_profile_id, tags,
  series_type, season_folder, monitor_mode, search_missing, search_cutoff,
  minimum_availability, search_for_movie,
  created_at_ts, updated_at_ts
FROM arr_applications;

DROP TABLE arr_applications;
ALTER TABLE arr_applications_new RENAME TO arr_applications;

CREATE UNIQUE INDEX IF NOT EXISTS idx_arr_apps_default_per_type
ON arr_applications(type, is_default) WHERE is_default = 1;
