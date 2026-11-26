-- Additive only: existing sources retain the Torznab path and configuration.
ALTER TABLE sources ADD COLUMN source_kind TEXT NOT NULL DEFAULT 'torznab' CHECK(source_kind IN ('torznab','rss'));
CREATE TABLE direct_rss_feeds (
 id INTEGER PRIMARY KEY AUTOINCREMENT,
 source_id INTEGER NOT NULL REFERENCES sources(id) ON DELETE CASCADE,
 url_encrypted TEXT NOT NULL,
 name TEXT,
 category_key TEXT NOT NULL,
 enabled INTEGER NOT NULL DEFAULT 1,
 last_status TEXT,
 last_error TEXT,
 last_checked_at_ts INTEGER,
 last_item_count INTEGER,
 potentially_saturated INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX idx_direct_rss_feeds_source ON direct_rss_feeds(source_id);
-- Provenance survives feed deletion. No FK to feeds, and never cascade to releases.
CREATE TABLE direct_rss_provenance (
 source_id INTEGER NOT NULL REFERENCES sources(id) ON DELETE CASCADE,
 guid TEXT NOT NULL,
 feed_id INTEGER NOT NULL,
 PRIMARY KEY(source_id,guid,feed_id),
 FOREIGN KEY(source_id,guid) REFERENCES releases(source_id,guid) ON DELETE CASCADE
);
