-- Precomputed top feed fields to reduce CPU/temporary b-trees in /api/feed/top.

ALTER TABLE releases ADD COLUMN title_normalized TEXT;
ALTER TABLE releases ADD COLUMN dedupe_key TEXT;
ALTER TABLE releases ADD COLUMN top_category_key TEXT;

-- Backfill normalized title once for existing rows.
UPDATE releases
SET title_normalized = lower(trim(COALESCE(title_clean, title, '')))
WHERE title_normalized IS NULL OR title_normalized = '';

-- Backfill dedupe key once for existing rows.
UPDATE releases
SET dedupe_key = CASE
    WHEN COALESCE(title_normalized, '') <> ''
      THEN 'title_year:' || title_normalized || '|' || COALESCE(CAST(year AS TEXT), '-') || '|' || lower(trim(COALESCE(media_type, '')))
    ELSE 'release:' || COALESCE(NULLIF(lower(trim(guid)), ''), CAST(id AS TEXT))
END
WHERE dedupe_key IS NULL OR dedupe_key = '';

-- Backfill top category from unified category first.
UPDATE releases
SET top_category_key = CASE unified_category
    WHEN 'Film' THEN 'films'
    WHEN 'Serie' THEN 'series'
    WHEN 'Emission' THEN 'emissions'
    WHEN 'Spectacle' THEN 'spectacle'
    WHEN 'JeuWindows' THEN 'games'
    WHEN 'Animation' THEN 'animation'
    WHEN 'Anime' THEN 'anime'
    WHEN 'Audio' THEN 'audio'
    WHEN 'Book' THEN 'books'
    WHEN 'Comic' THEN 'comics'
    ELSE top_category_key
END
WHERE top_category_key IS NULL OR top_category_key = '';

-- Fallback from source_category_mappings.group_key when unified category is absent.
UPDATE releases
SET top_category_key = (
  SELECT CASE lower(trim(COALESCE(scm.group_key, '')))
      WHEN '' THEN NULL
      WHEN 'movie' THEN 'films'
      WHEN 'movies' THEN 'films'
      WHEN 'film' THEN 'films'
      WHEN 'films' THEN 'films'
      WHEN 'tv' THEN 'series'
      WHEN 'serie' THEN 'series'
      WHEN 'series' THEN 'series'
      WHEN 'show' THEN 'emissions'
      WHEN 'shows' THEN 'emissions'
      WHEN 'emission' THEN 'emissions'
      WHEN 'emissions' THEN 'emissions'
      WHEN 'game' THEN 'games'
      WHEN 'games' THEN 'games'
      WHEN 'book' THEN 'books'
      WHEN 'books' THEN 'books'
      WHEN 'comic' THEN 'comics'
      WHEN 'comics' THEN 'comics'
      WHEN 'animation' THEN 'animation'
      WHEN 'anime' THEN 'anime'
      WHEN 'audio' THEN 'audio'
      WHEN 'spectacle' THEN 'spectacle'
      ELSE lower(trim(scm.group_key))
  END
  FROM source_category_mappings scm
  WHERE scm.source_id = releases.source_id
    AND scm.cat_id = releases.category_id
  LIMIT 1
)
WHERE (top_category_key IS NULL OR top_category_key = '')
  AND EXISTS (
      SELECT 1
      FROM source_category_mappings scm
      WHERE scm.source_id = releases.source_id
        AND scm.cat_id = releases.category_id
  );

-- Final fallback from legacy source_categories.unified_key.
UPDATE releases
SET top_category_key = (
  SELECT CASE lower(trim(COALESCE(sc.unified_key, '')))
      WHEN '' THEN NULL
      WHEN 'movie' THEN 'films'
      WHEN 'movies' THEN 'films'
      WHEN 'film' THEN 'films'
      WHEN 'films' THEN 'films'
      WHEN 'tv' THEN 'series'
      WHEN 'serie' THEN 'series'
      WHEN 'series' THEN 'series'
      WHEN 'show' THEN 'emissions'
      WHEN 'shows' THEN 'emissions'
      WHEN 'emission' THEN 'emissions'
      WHEN 'emissions' THEN 'emissions'
      WHEN 'game' THEN 'games'
      WHEN 'games' THEN 'games'
      WHEN 'book' THEN 'books'
      WHEN 'books' THEN 'books'
      WHEN 'comic' THEN 'comics'
      WHEN 'comics' THEN 'comics'
      WHEN 'animation' THEN 'animation'
      WHEN 'anime' THEN 'anime'
      WHEN 'audio' THEN 'audio'
      WHEN 'spectacle' THEN 'spectacle'
      ELSE lower(trim(sc.unified_key))
  END
  FROM source_categories sc
  WHERE sc.source_id = releases.source_id
    AND sc.cat_id = releases.category_id
  LIMIT 1
)
WHERE (top_category_key IS NULL OR top_category_key = '')
  AND EXISTS (
      SELECT 1
      FROM source_categories sc
      WHERE sc.source_id = releases.source_id
        AND sc.cat_id = releases.category_id
  );

CREATE INDEX IF NOT EXISTS idx_releases_top_category_date
ON releases(top_category_key, published_at_ts DESC);

CREATE INDEX IF NOT EXISTS idx_releases_dedupe
ON releases(dedupe_key);
