-- Extend arr_applications types to support request apps (Overseerr/Jellyseerr)
-- NOTE:
--   arr_library_items / arr_sync_status have ON DELETE CASCADE foreign keys to arr_applications.
--   During parent-table replacement, dropping arr_applications would cascade-delete child rows.
--   We preserve child data in backup tables and restore them after the parent swap.

CREATE TABLE IF NOT EXISTS arr_applications_new (
  id                  INTEGER PRIMARY KEY AUTOINCREMENT,
  type                TEXT NOT NULL CHECK(type IN ('sonarr', 'radarr', 'overseerr', 'jellyseerr')),
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

-- Preserve child data before replacing parent table.
CREATE TABLE IF NOT EXISTS arr_library_items_backup AS
SELECT
  id, app_id, type, tmdb_id, tvdb_id, internal_id,
  title, original_title, title_slug, alternate_titles, title_normalized,
  added_at, synced_at
FROM arr_library_items;

CREATE TABLE IF NOT EXISTS arr_sync_status_backup AS
SELECT
  app_id, last_sync_at, last_sync_count, last_error
FROM arr_sync_status;

DROP TABLE IF EXISTS arr_library_items;
DROP TABLE IF EXISTS arr_sync_status;

DROP TABLE arr_applications;
ALTER TABLE arr_applications_new RENAME TO arr_applications;

CREATE UNIQUE INDEX IF NOT EXISTS idx_arr_apps_default_per_type
ON arr_applications(type, is_default) WHERE is_default = 1;

-- Recreate child tables and restore rows.
CREATE TABLE IF NOT EXISTS arr_library_items (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  app_id INTEGER NOT NULL,
  type TEXT NOT NULL CHECK (type IN ('movie', 'series')),
  tmdb_id INTEGER,
  tvdb_id INTEGER,
  internal_id INTEGER NOT NULL,
  title TEXT NOT NULL,
  original_title TEXT,
  title_slug TEXT,
  alternate_titles TEXT,
  title_normalized TEXT,
  added_at TEXT NOT NULL DEFAULT (datetime('now')),
  synced_at TEXT NOT NULL DEFAULT (datetime('now')),
  FOREIGN KEY (app_id) REFERENCES arr_applications(id) ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS idx_arr_library_app_id ON arr_library_items(app_id);
CREATE INDEX IF NOT EXISTS idx_arr_library_tmdb_id ON arr_library_items(tmdb_id) WHERE tmdb_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS idx_arr_library_tvdb_id ON arr_library_items(tvdb_id) WHERE tvdb_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS idx_arr_library_title_normalized ON arr_library_items(title_normalized);
CREATE INDEX IF NOT EXISTS idx_arr_library_type ON arr_library_items(type);

CREATE TABLE IF NOT EXISTS arr_sync_status (
  app_id INTEGER PRIMARY KEY,
  last_sync_at TEXT,
  last_sync_count INTEGER DEFAULT 0,
  last_error TEXT,
  FOREIGN KEY (app_id) REFERENCES arr_applications(id) ON DELETE CASCADE
);

INSERT INTO arr_library_items(
  id, app_id, type, tmdb_id, tvdb_id, internal_id,
  title, original_title, title_slug, alternate_titles, title_normalized,
  added_at, synced_at
)
SELECT
  id, app_id, type, tmdb_id, tvdb_id, internal_id,
  title, original_title, title_slug, alternate_titles, title_normalized,
  added_at, synced_at
FROM arr_library_items_backup;

INSERT INTO arr_sync_status(app_id, last_sync_at, last_sync_count, last_error)
SELECT app_id, last_sync_at, last_sync_count, last_error
FROM arr_sync_status_backup;

DROP TABLE IF EXISTS arr_library_items_backup;
DROP TABLE IF EXISTS arr_sync_status_backup;
