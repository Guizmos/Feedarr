PRAGMA foreign_keys=ON;

ALTER TABLE releases ADD COLUMN poster_provider TEXT;
ALTER TABLE releases ADD COLUMN poster_provider_id TEXT;
ALTER TABLE releases ADD COLUMN poster_lang TEXT;
ALTER TABLE releases ADD COLUMN poster_size TEXT;
ALTER TABLE releases ADD COLUMN poster_hash TEXT;
ALTER TABLE releases ADD COLUMN poster_last_attempt_ts INTEGER;
ALTER TABLE releases ADD COLUMN poster_last_error TEXT;
