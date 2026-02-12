-- Arr Applications (Sonarr/Radarr) configuration
CREATE TABLE IF NOT EXISTS arr_applications (
  id                  INTEGER PRIMARY KEY AUTOINCREMENT,
  type                TEXT NOT NULL CHECK(type IN ('sonarr', 'radarr')),
  name                TEXT,
  base_url            TEXT NOT NULL,
  api_key_encrypted   TEXT NOT NULL,
  is_enabled          INTEGER NOT NULL DEFAULT 1,
  is_default          INTEGER NOT NULL DEFAULT 0,

  -- Default settings
  root_folder_path    TEXT,
  quality_profile_id  INTEGER,
  tags                TEXT,  -- JSON array of tag IDs

  -- Sonarr-specific defaults
  series_type         TEXT,  -- standard, daily, anime
  season_folder       INTEGER DEFAULT 1,
  monitor_mode        TEXT,  -- all, future, missing, existing, firstSeason, lastSeason, pilot, none
  search_missing      INTEGER DEFAULT 1,
  search_cutoff       INTEGER DEFAULT 0,

  -- Radarr-specific defaults
  minimum_availability TEXT,  -- announced, inCinemas, released
  search_for_movie     INTEGER DEFAULT 1,

  created_at_ts       INTEGER NOT NULL,
  updated_at_ts       INTEGER NOT NULL
);

-- Ensure only one default per type
CREATE UNIQUE INDEX IF NOT EXISTS idx_arr_apps_default_per_type
ON arr_applications(type, is_default) WHERE is_default = 1;
