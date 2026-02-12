PRAGMA foreign_keys=ON;

CREATE TABLE IF NOT EXISTS schema_migrations (
  id TEXT PRIMARY KEY,
  applied_at_ts INTEGER NOT NULL
);

CREATE TABLE IF NOT EXISTS sources (
  id                INTEGER PRIMARY KEY AUTOINCREMENT,
  name              TEXT NOT NULL,
  enabled           INTEGER NOT NULL DEFAULT 1,

  torznab_url        TEXT NOT NULL,
  api_key            TEXT NOT NULL,
  auth_mode          TEXT NOT NULL DEFAULT 'query',

  created_at_ts      INTEGER NOT NULL,
  updated_at_ts      INTEGER NOT NULL,

  last_sync_at_ts    INTEGER,
  last_status        TEXT,
  last_error         TEXT,
  last_item_count    INTEGER DEFAULT 0,

  last_caps_sync_at_ts INTEGER,
  caps_hash            TEXT
);

CREATE UNIQUE INDEX IF NOT EXISTS idx_sources_unique_url
ON sources(torznab_url);

CREATE TABLE IF NOT EXISTS releases (
  id               INTEGER PRIMARY KEY AUTOINCREMENT,
  source_id        INTEGER NOT NULL,

  guid             TEXT NOT NULL,
  title            TEXT NOT NULL,
  link             TEXT,
  published_at_ts  INTEGER,

  size_bytes       INTEGER,
  seeders          INTEGER,
  leechers         INTEGER,
  info_hash        TEXT,
  download_url     TEXT,

  category_id      INTEGER,

  raw_json         TEXT,

  seen             INTEGER NOT NULL DEFAULT 0,
  created_at_ts    INTEGER NOT NULL,

  FOREIGN KEY(source_id) REFERENCES sources(id) ON DELETE CASCADE
);

CREATE UNIQUE INDEX IF NOT EXISTS idx_releases_source_guid
ON releases(source_id, guid);

CREATE INDEX IF NOT EXISTS idx_releases_published
ON releases(published_at_ts);

CREATE INDEX IF NOT EXISTS idx_releases_seeders
ON releases(seeders);

CREATE TABLE IF NOT EXISTS activity_logs (
  id              INTEGER PRIMARY KEY AUTOINCREMENT,
  created_at_ts   INTEGER NOT NULL,
  level           TEXT NOT NULL,
  source_id       INTEGER,
  message         TEXT NOT NULL,
  details_json    TEXT,
  FOREIGN KEY(source_id) REFERENCES sources(id) ON DELETE SET NULL
);

CREATE INDEX IF NOT EXISTS idx_activity_created
ON activity_logs(created_at_ts);
