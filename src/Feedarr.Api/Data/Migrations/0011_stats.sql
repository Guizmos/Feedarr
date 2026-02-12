-- Table for persistent statistics (providers, indexers, etc.)
CREATE TABLE IF NOT EXISTS stats (
  key TEXT PRIMARY KEY,
  value INTEGER NOT NULL DEFAULT 0,
  updated_at_ts INTEGER NOT NULL
);

-- Initialize provider stats
INSERT OR IGNORE INTO stats (key, value, updated_at_ts) VALUES ('tmdb_calls', 0, 0);
INSERT OR IGNORE INTO stats (key, value, updated_at_ts) VALUES ('tmdb_failures', 0, 0);
INSERT OR IGNORE INTO stats (key, value, updated_at_ts) VALUES ('fanart_calls', 0, 0);
INSERT OR IGNORE INTO stats (key, value, updated_at_ts) VALUES ('fanart_failures', 0, 0);
INSERT OR IGNORE INTO stats (key, value, updated_at_ts) VALUES ('igdb_calls', 0, 0);
INSERT OR IGNORE INTO stats (key, value, updated_at_ts) VALUES ('igdb_failures', 0, 0);

-- Initialize indexer stats
INSERT OR IGNORE INTO stats (key, value, updated_at_ts) VALUES ('indexer_queries', 0, 0);
INSERT OR IGNORE INTO stats (key, value, updated_at_ts) VALUES ('indexer_failures', 0, 0);
INSERT OR IGNORE INTO stats (key, value, updated_at_ts) VALUES ('sync_jobs', 0, 0);
INSERT OR IGNORE INTO stats (key, value, updated_at_ts) VALUES ('sync_failures', 0, 0);
