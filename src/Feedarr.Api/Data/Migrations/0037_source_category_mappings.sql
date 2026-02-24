CREATE TABLE IF NOT EXISTS source_category_mappings (
  source_id      INTEGER NOT NULL,
  cat_id         INTEGER NOT NULL,
  group_key      TEXT,
  group_label    TEXT,
  created_at_ts  INTEGER NOT NULL,
  updated_at_ts  INTEGER NOT NULL,
  PRIMARY KEY (source_id, cat_id),
  FOREIGN KEY(source_id) REFERENCES sources(id) ON DELETE CASCADE,
  CHECK (
    group_key IS NULL OR lower(group_key) IN (
      'films',
      'series',
      'animation',
      'anime',
      'comics',
      'books',
      'audio',
      'spectacle',
      'emissions'
    )
  )
);

CREATE INDEX IF NOT EXISTS idx_source_category_mappings_source
ON source_category_mappings(source_id);

CREATE INDEX IF NOT EXISTS idx_source_category_mappings_group
ON source_category_mappings(group_key);
