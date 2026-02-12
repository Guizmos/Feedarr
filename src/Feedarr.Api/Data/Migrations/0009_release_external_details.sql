PRAGMA foreign_keys=ON;

ALTER TABLE releases ADD COLUMN ext_provider TEXT;
ALTER TABLE releases ADD COLUMN ext_provider_id TEXT;
ALTER TABLE releases ADD COLUMN ext_title TEXT;
ALTER TABLE releases ADD COLUMN ext_overview TEXT;
ALTER TABLE releases ADD COLUMN ext_tagline TEXT;
ALTER TABLE releases ADD COLUMN ext_genres TEXT;
ALTER TABLE releases ADD COLUMN ext_release_date TEXT;
ALTER TABLE releases ADD COLUMN ext_runtime_minutes INTEGER;
ALTER TABLE releases ADD COLUMN ext_rating REAL;
ALTER TABLE releases ADD COLUMN ext_votes INTEGER;
ALTER TABLE releases ADD COLUMN ext_updated_at_ts INTEGER;
