WITH legacy_prepared AS (
  SELECT
    sc.source_id AS source_id,
    sc.cat_id AS cat_id,
    CASE
      WHEN sc.unified_key IS NULL OR trim(sc.unified_key) = '' THEN NULL
      WHEN lower(trim(sc.unified_key)) IN ('movie', 'movies', 'film', 'films') THEN 'films'
      WHEN lower(trim(sc.unified_key)) IN ('tv', 'serie', 'series') THEN 'series'
      WHEN lower(trim(sc.unified_key)) IN ('show', 'shows', 'emission', 'emissions') THEN 'emissions'
      WHEN lower(trim(sc.unified_key)) IN ('game', 'games') THEN 'games'
      WHEN lower(trim(sc.unified_key)) IN ('book', 'books') THEN 'books'
      WHEN lower(trim(sc.unified_key)) IN ('comic', 'comics') THEN 'comics'
      WHEN lower(trim(sc.unified_key)) = 'animation' THEN 'animation'
      WHEN lower(trim(sc.unified_key)) = 'anime' THEN 'anime'
      WHEN lower(trim(sc.unified_key)) = 'audio' THEN 'audio'
      WHEN lower(trim(sc.unified_key)) = 'spectacle' THEN 'spectacle'
      ELSE NULL
    END AS normalized_group_key,
    CASE
      WHEN sc.unified_key IS NULL OR trim(sc.unified_key) = '' THEN NULL
      WHEN lower(trim(sc.unified_key)) IN ('movie', 'movies', 'film', 'films') THEN 'Films'
      WHEN lower(trim(sc.unified_key)) IN ('tv', 'serie', 'series') THEN 'Série TV'
      WHEN lower(trim(sc.unified_key)) IN ('show', 'shows', 'emission', 'emissions') THEN 'Emissions'
      WHEN lower(trim(sc.unified_key)) IN ('game', 'games') THEN 'Jeux PC'
      WHEN lower(trim(sc.unified_key)) IN ('book', 'books') THEN 'Livres'
      WHEN lower(trim(sc.unified_key)) IN ('comic', 'comics') THEN 'Comics'
      WHEN lower(trim(sc.unified_key)) = 'animation' THEN 'Animation'
      WHEN lower(trim(sc.unified_key)) = 'anime' THEN 'Anime'
      WHEN lower(trim(sc.unified_key)) = 'audio' THEN 'Audio'
      WHEN lower(trim(sc.unified_key)) = 'spectacle' THEN 'Spectacle'
      ELSE NULL
    END AS canonical_group_label,
    NULLIF(trim(sc.unified_label), '') AS legacy_unified_label,
    NULLIF(trim(sc.name), '') AS legacy_category_name
  FROM source_categories sc
  WHERE sc.source_id > 0
    AND sc.cat_id > 0
)
INSERT INTO source_category_mappings(
  source_id,
  cat_id,
  group_key,
  group_label,
  created_at_ts,
  updated_at_ts
)
SELECT
  source_id,
  cat_id,
  normalized_group_key,
  COALESCE(canonical_group_label, legacy_unified_label, legacy_category_name),
  CAST(strftime('%s', 'now') AS INTEGER),
  CAST(strftime('%s', 'now') AS INTEGER)
FROM legacy_prepared
WHERE 1 = 1
ON CONFLICT(source_id, cat_id) DO UPDATE SET
  group_key = CASE
    WHEN (source_category_mappings.group_key IS NULL OR trim(source_category_mappings.group_key) = '')
      AND excluded.group_key IS NOT NULL
      THEN excluded.group_key
    ELSE source_category_mappings.group_key
  END,
  group_label = CASE
    WHEN (source_category_mappings.group_label IS NULL OR trim(source_category_mappings.group_label) = '')
      AND excluded.group_label IS NOT NULL
      THEN excluded.group_label
    ELSE source_category_mappings.group_label
  END,
  updated_at_ts = CASE
    WHEN (
      ((source_category_mappings.group_key IS NULL OR trim(source_category_mappings.group_key) = '') AND excluded.group_key IS NOT NULL)
      OR
      ((source_category_mappings.group_label IS NULL OR trim(source_category_mappings.group_label) = '') AND excluded.group_label IS NOT NULL)
    )
      THEN excluded.updated_at_ts
    ELSE source_category_mappings.updated_at_ts
  END
;
