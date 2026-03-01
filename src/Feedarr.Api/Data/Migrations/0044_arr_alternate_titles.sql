CREATE TABLE IF NOT EXISTS arr_alternate_titles (
    app_id INTEGER NOT NULL,
    type TEXT NOT NULL CHECK (type IN ('movie', 'series')),
    internal_id INTEGER NOT NULL,
    title_norm TEXT NOT NULL,
    title_raw TEXT,
    FOREIGN KEY (app_id) REFERENCES arr_applications(id) ON DELETE CASCADE,
    UNIQUE(app_id, type, internal_id, title_norm) ON CONFLICT IGNORE
);

CREATE INDEX IF NOT EXISTS idx_arr_alt_titles_lookup
ON arr_alternate_titles(type, title_norm, app_id);

CREATE INDEX IF NOT EXISTS idx_arr_alt_titles_internal
ON arr_alternate_titles(app_id, type, internal_id);

CREATE INDEX IF NOT EXISTS idx_arr_library_app_type_internal
ON arr_library_items(app_id, type, internal_id);
