ALTER TABLE releases ADD COLUMN std_category_id INTEGER;
ALTER TABLE releases ADD COLUMN spec_category_id INTEGER;
ALTER TABLE releases ADD COLUMN unified_category TEXT;
ALTER TABLE releases ADD COLUMN category_ids TEXT;

CREATE INDEX IF NOT EXISTS idx_releases_std_category_id ON releases(std_category_id);
CREATE INDEX IF NOT EXISTS idx_releases_spec_category_id ON releases(spec_category_id);
CREATE INDEX IF NOT EXISTS idx_releases_unified_category ON releases(unified_category);
