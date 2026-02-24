-- Enforce canonical label for anime categories in source_categories.
-- Idempotent: safe if labels are already corrected.
UPDATE source_categories
SET unified_label = 'Anime'
WHERE unified_key = 'anime'
  AND (unified_label IS NULL OR unified_label <> 'Anime');
