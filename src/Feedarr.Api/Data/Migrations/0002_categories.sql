CREATE TABLE IF NOT EXISTS source_categories (
  id               INTEGER PRIMARY KEY AUTOINCREMENT,
  source_id        INTEGER NOT NULL,

  cat_id           INTEGER NOT NULL,
  name             TEXT NOT NULL,

  parent_cat_id    INTEGER,
  is_sub           INTEGER NOT NULL DEFAULT 0,

  last_seen_at_ts  INTEGER,
  seen_count_7d    INTEGER NOT NULL DEFAULT 0,

  FOREIGN KEY(source_id) REFERENCES sources(id) ON DELETE CASCADE
);

CREATE UNIQUE INDEX IF NOT EXISTS idx_source_categories_unique
ON source_categories(source_id, cat_id);
