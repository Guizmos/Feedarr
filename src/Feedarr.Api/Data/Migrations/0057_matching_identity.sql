-- Additive only: historical identities and files are deliberately left untouched.
ALTER TABLE media_entities ADD COLUMN tmdb_media_type TEXT;
ALTER TABLE releases ADD COLUMN tmdb_media_type TEXT;
ALTER TABLE media_entities ADD COLUMN manual_match_provider TEXT;
ALTER TABLE media_entities ADD COLUMN manual_match_type TEXT;
ALTER TABLE media_entities ADD COLUMN manual_match_id TEXT;
ALTER TABLE media_entities ADD COLUMN match_revision TEXT;
