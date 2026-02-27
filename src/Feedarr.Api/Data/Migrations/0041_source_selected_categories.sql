CREATE TABLE IF NOT EXISTS source_selected_categories (
  source_id INTEGER NOT NULL,
  cat_id INTEGER NOT NULL,
  PRIMARY KEY (source_id, cat_id),
  FOREIGN KEY (source_id) REFERENCES sources(id) ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS idx_source_selected_categories_source
ON source_selected_categories(source_id);

-- Backfill compatibility:
-- - Only for sources that do not already have an explicit selection.
-- - We seed from mapped categories (group_key non-empty), because they are the
--   closest persisted signal of user-intent with the legacy schema.
INSERT OR IGNORE INTO source_selected_categories(source_id, cat_id)
SELECT scm.source_id, scm.cat_id
FROM source_category_mappings scm
WHERE scm.cat_id > 0
  AND scm.group_key IS NOT NULL
  AND trim(scm.group_key) <> ''
  AND NOT EXISTS (
    SELECT 1
    FROM source_selected_categories ssc
    WHERE ssc.source_id = scm.source_id
  );
