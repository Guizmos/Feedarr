PRAGMA foreign_keys=ON;

ALTER TABLE releases ADD COLUMN title_manual_override INTEGER NOT NULL DEFAULT 0;

CREATE INDEX IF NOT EXISTS idx_releases_title_manual_override
ON releases(title_manual_override);
