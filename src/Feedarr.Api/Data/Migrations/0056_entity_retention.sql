-- Unknown historical provenance is deliberately protected, never inferred from age.
ALTER TABLE media_entities ADD COLUMN retention_origin TEXT NOT NULL DEFAULT 'legacy_unknown';
ALTER TABLE media_entities ADD COLUMN orphaned_at_ts INTEGER;
CREATE INDEX idx_media_entities_orphan_retention ON media_entities(retention_origin, orphaned_at_ts, id);
CREATE INDEX IF NOT EXISTS idx_poster_matches_file ON poster_matches(poster_file COLLATE NOCASE);

CREATE TRIGGER trg_entity_orphan_created AFTER INSERT ON media_entities
BEGIN
  UPDATE media_entities SET orphaned_at_ts=unixepoch()
  WHERE id=NEW.id AND NOT EXISTS (SELECT 1 FROM releases WHERE entity_id=NEW.id);
END;
CREATE TRIGGER trg_entity_used_insert AFTER INSERT ON releases WHEN NEW.entity_id IS NOT NULL
BEGIN
  UPDATE media_entities SET orphaned_at_ts=NULL WHERE id=NEW.entity_id;
END;
CREATE TRIGGER trg_entity_used_update AFTER UPDATE OF entity_id ON releases
WHEN NEW.entity_id IS NOT OLD.entity_id
BEGIN
  UPDATE media_entities SET orphaned_at_ts=NULL WHERE id=NEW.entity_id;
  UPDATE media_entities SET orphaned_at_ts=unixepoch() WHERE id=OLD.entity_id
    AND NOT EXISTS (SELECT 1 FROM releases WHERE entity_id=OLD.entity_id);
END;
CREATE TRIGGER trg_entity_unused_delete AFTER DELETE ON releases WHEN OLD.entity_id IS NOT NULL
BEGIN
  UPDATE media_entities SET orphaned_at_ts=unixepoch() WHERE id=OLD.entity_id
    AND NOT EXISTS (SELECT 1 FROM releases WHERE entity_id=OLD.entity_id);
END;
CREATE INDEX idx_poster_matches_history_title ON poster_matches(normalized_title, year);
