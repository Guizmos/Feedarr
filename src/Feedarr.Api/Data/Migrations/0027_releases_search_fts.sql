-- Full-text search index for release title queries used by /api/feed/{sourceId}?q=
CREATE VIRTUAL TABLE IF NOT EXISTS releases_fts
USING fts5(
  title,
  title_clean,
  content='releases',
  content_rowid='id'
);

INSERT INTO releases_fts(releases_fts) VALUES('rebuild');

CREATE TRIGGER IF NOT EXISTS releases_ai
AFTER INSERT ON releases
BEGIN
  INSERT INTO releases_fts(rowid, title, title_clean)
  VALUES (new.id, COALESCE(new.title, ''), COALESCE(new.title_clean, ''));
END;

CREATE TRIGGER IF NOT EXISTS releases_ad
AFTER DELETE ON releases
BEGIN
  INSERT INTO releases_fts(releases_fts, rowid, title, title_clean)
  VALUES ('delete', old.id, COALESCE(old.title, ''), COALESCE(old.title_clean, ''));
END;

CREATE TRIGGER IF NOT EXISTS releases_au
AFTER UPDATE ON releases
BEGIN
  INSERT INTO releases_fts(releases_fts, rowid, title, title_clean)
  VALUES ('delete', old.id, COALESCE(old.title, ''), COALESCE(old.title_clean, ''));
  INSERT INTO releases_fts(rowid, title, title_clean)
  VALUES (new.id, COALESCE(new.title, ''), COALESCE(new.title_clean, ''));
END;
