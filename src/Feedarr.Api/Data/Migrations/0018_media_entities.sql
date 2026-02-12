PRAGMA foreign_keys=ON;

CREATE TABLE IF NOT EXISTS media_entities (
  id                   INTEGER PRIMARY KEY AUTOINCREMENT,
  unified_category      TEXT NOT NULL,
  title_clean           TEXT NOT NULL,
  year                 INTEGER,
  poster_file           TEXT,
  poster_updated_at_ts  INTEGER,
  created_at_ts         INTEGER NOT NULL,
  updated_at_ts         INTEGER NOT NULL
);

CREATE UNIQUE INDEX IF NOT EXISTS idx_media_entities_key
ON media_entities(unified_category, title_clean, IFNULL(year, -1));

ALTER TABLE releases ADD COLUMN entity_id INTEGER;

CREATE INDEX IF NOT EXISTS idx_releases_entity_id
ON releases(entity_id);
