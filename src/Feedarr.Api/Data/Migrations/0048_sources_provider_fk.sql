-- FEEDARR:NO-FK
-- Ajoute la contrainte FK provider_id → providers(id) ON DELETE SET NULL sur la table sources.
-- Nécessite FK=OFF (marqueur FEEDARR:NO-FK) pour que le DROP TABLE sources ne déclenche pas
-- le ON DELETE CASCADE de releases vers sources.

CREATE TABLE sources_new (
  id                   INTEGER PRIMARY KEY AUTOINCREMENT,
  name                 TEXT    NOT NULL,
  enabled              INTEGER NOT NULL DEFAULT 1,
  torznab_url          TEXT    NOT NULL,
  api_key              TEXT    NOT NULL,
  auth_mode            TEXT    NOT NULL DEFAULT 'query',
  created_at_ts        INTEGER NOT NULL,
  updated_at_ts        INTEGER NOT NULL,
  last_sync_at_ts      INTEGER,
  last_status          TEXT,
  last_error           TEXT,
  last_item_count      INTEGER DEFAULT 0,
  last_caps_sync_at_ts INTEGER,
  caps_hash            TEXT,
  rss_mode             TEXT,
  provider_id          INTEGER,
  color                TEXT,
  FOREIGN KEY(provider_id) REFERENCES providers(id) ON DELETE SET NULL
);

INSERT INTO sources_new
SELECT id, name, enabled, torznab_url, api_key, auth_mode,
       created_at_ts, updated_at_ts, last_sync_at_ts, last_status,
       last_error, last_item_count, last_caps_sync_at_ts, caps_hash,
       rss_mode, provider_id, color
FROM sources;

DROP TABLE sources;
ALTER TABLE sources_new RENAME TO sources;

CREATE UNIQUE INDEX IF NOT EXISTS idx_sources_unique_url ON sources(torznab_url);
CREATE INDEX IF NOT EXISTS idx_sources_provider_id ON sources(provider_id);
