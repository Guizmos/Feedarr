CREATE TABLE IF NOT EXISTS poster_matches (
  fingerprint TEXT PRIMARY KEY,
  media_type TEXT,
  normalized_title TEXT,
  year INTEGER,
  season INTEGER,
  episode INTEGER,
  ids_json TEXT,
  confidence REAL,
  match_source TEXT,
  poster_file TEXT,
  poster_provider TEXT,
  poster_provider_id TEXT,
  poster_lang TEXT,
  poster_size TEXT,
  created_ts INTEGER NOT NULL,
  last_seen_ts INTEGER NOT NULL,
  last_attempt_ts INTEGER,
  last_error TEXT
);

CREATE INDEX IF NOT EXISTS idx_poster_matches_title ON poster_matches(media_type, normalized_title, year);
