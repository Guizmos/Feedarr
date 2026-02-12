PRAGMA foreign_keys=ON;

CREATE TABLE IF NOT EXISTS providers (
  id                INTEGER PRIMARY KEY AUTOINCREMENT,
  type              TEXT NOT NULL,
  name              TEXT NOT NULL,
  base_url          TEXT NOT NULL,
  api_key           TEXT NOT NULL,
  enabled           INTEGER NOT NULL DEFAULT 1,
  last_test_ok_at_ts INTEGER,
  created_at_ts     INTEGER NOT NULL,
  updated_at_ts     INTEGER NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_providers_type
ON providers(type);

CREATE INDEX IF NOT EXISTS idx_providers_base_url
ON providers(base_url);

CREATE INDEX IF NOT EXISTS idx_providers_enabled
ON providers(enabled);

ALTER TABLE sources ADD COLUMN provider_id INTEGER;

CREATE INDEX IF NOT EXISTS idx_sources_provider_id
ON sources(provider_id);
