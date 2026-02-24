CREATE TABLE IF NOT EXISTS source_category_mappings_v2 (
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
      'games',
      'comics',
      'books',
      'audio',
      'spectacle',
      'emissions'
    )
  )
);

INSERT INTO source_category_mappings_v2 (
  source_id, cat_id, group_key, group_label, created_at_ts, updated_at_ts
)
SELECT
  source_id,
  cat_id,
  CASE
    WHEN group_key IS NULL OR trim(group_key) = '' THEN NULL
    WHEN lower(trim(group_key)) IN ('movie', 'movies', 'film', 'films') THEN 'films'
    WHEN lower(trim(group_key)) IN ('tv', 'serie', 'series') THEN 'series'
    WHEN lower(trim(group_key)) IN ('show', 'shows', 'emission', 'emissions') THEN 'emissions'
    WHEN lower(trim(group_key)) IN ('game', 'games') THEN 'games'
    WHEN lower(trim(group_key)) IN ('book', 'books') THEN 'books'
    WHEN lower(trim(group_key)) IN ('comic', 'comics') THEN 'comics'
    WHEN lower(trim(group_key)) IN ('animation', 'anime', 'audio', 'spectacle') THEN lower(trim(group_key))
    ELSE NULL
  END AS normalized_group_key,
  CASE
    WHEN group_key IS NULL OR trim(group_key) = '' THEN NULL
    WHEN lower(trim(group_key)) IN ('movie', 'movies', 'film', 'films') THEN 'Films'
    WHEN lower(trim(group_key)) IN ('tv', 'serie', 'series') THEN 'Série TV'
    WHEN lower(trim(group_key)) IN ('show', 'shows', 'emission', 'emissions') THEN 'Emissions'
    WHEN lower(trim(group_key)) IN ('game', 'games') THEN 'Jeux PC'
    WHEN lower(trim(group_key)) IN ('book', 'books') THEN 'Livres'
    WHEN lower(trim(group_key)) IN ('comic', 'comics') THEN 'Comics'
    WHEN lower(trim(group_key)) = 'animation' THEN 'Animation'
    WHEN lower(trim(group_key)) = 'anime' THEN 'Anime'
    WHEN lower(trim(group_key)) = 'audio' THEN 'Audio'
    WHEN lower(trim(group_key)) = 'spectacle' THEN 'Spectacle'
    ELSE NULL
  END AS normalized_group_label,
  created_at_ts,
  updated_at_ts
FROM source_category_mappings;

DROP TABLE source_category_mappings;
ALTER TABLE source_category_mappings_v2 RENAME TO source_category_mappings;

CREATE INDEX IF NOT EXISTS idx_source_category_mappings_source
ON source_category_mappings(source_id);

CREATE INDEX IF NOT EXISTS idx_source_category_mappings_group
ON source_category_mappings(group_key);
