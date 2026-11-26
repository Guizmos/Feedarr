-- Root provenance is release-owned: reused rows can never become new donors.
CREATE TABLE local_identity_reuse (
  release_id INTEGER PRIMARY KEY REFERENCES releases(id) ON DELETE CASCADE,
  donor_release_id INTEGER NOT NULL,
  donor_source_id INTEGER NOT NULL,
  donor_entity_id INTEGER NOT NULL,
  provider TEXT NOT NULL,
  provider_id TEXT NOT NULL,
  evidence TEXT NOT NULL,
  reused_at_ts INTEGER NOT NULL
);
CREATE INDEX idx_releases_reuse_title ON releases(title COLLATE NOCASE);
CREATE INDEX idx_releases_reuse_hash ON releases(lower(info_hash)) WHERE info_hash IS NOT NULL;
