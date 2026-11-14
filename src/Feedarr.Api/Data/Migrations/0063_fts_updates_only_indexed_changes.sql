-- Keep the existing FTS index and INSERT/DELETE triggers. Replace only the
-- unconditional UPDATE trigger; no rebuild is needed for this optimization.
DROP TRIGGER IF EXISTS releases_au;

CREATE TRIGGER releases_au
AFTER UPDATE OF id, title, title_clean ON releases
WHEN old.id IS NOT new.id
  OR COALESCE(old.title, '') IS NOT COALESCE(new.title, '')
  OR COALESCE(old.title_clean, '') IS NOT COALESCE(new.title_clean, '')
BEGIN
  INSERT INTO releases_fts(releases_fts, rowid, title, title_clean)
  VALUES ('delete', old.id, COALESCE(old.title, ''), COALESCE(old.title_clean, ''));
  INSERT INTO releases_fts(rowid, title, title_clean)
  VALUES (new.id, COALESCE(new.title, ''), COALESCE(new.title_clean, ''));
END;
