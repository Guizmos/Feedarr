-- Persistent storage for Sonarr/Radarr library items
-- Allows immediate matching without API calls

CREATE TABLE IF NOT EXISTS arr_library_items (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    app_id INTEGER NOT NULL,
    type TEXT NOT NULL CHECK (type IN ('movie', 'series')),

    -- External IDs for matching
    tmdb_id INTEGER,
    tvdb_id INTEGER,

    -- Internal IDs from Sonarr/Radarr
    internal_id INTEGER NOT NULL,

    -- Titles for fallback matching
    title TEXT NOT NULL,
    original_title TEXT,
    title_slug TEXT,

    -- Alternate titles stored as JSON array: ["Title 1", "Title 2"]
    alternate_titles TEXT,

    -- Normalized titles for fast matching (pre-computed)
    title_normalized TEXT,

    -- Timestamps
    added_at TEXT NOT NULL DEFAULT (datetime('now')),
    synced_at TEXT NOT NULL DEFAULT (datetime('now')),

    FOREIGN KEY (app_id) REFERENCES arr_applications(id) ON DELETE CASCADE
);

-- Indexes for fast lookups
CREATE INDEX IF NOT EXISTS idx_arr_library_app_id ON arr_library_items(app_id);
CREATE INDEX IF NOT EXISTS idx_arr_library_tmdb_id ON arr_library_items(tmdb_id) WHERE tmdb_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS idx_arr_library_tvdb_id ON arr_library_items(tvdb_id) WHERE tvdb_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS idx_arr_library_title_normalized ON arr_library_items(title_normalized);
CREATE INDEX IF NOT EXISTS idx_arr_library_type ON arr_library_items(type);

-- Track last sync time per app
CREATE TABLE IF NOT EXISTS arr_sync_status (
    app_id INTEGER PRIMARY KEY,
    last_sync_at TEXT,
    last_sync_count INTEGER DEFAULT 0,
    last_error TEXT,
    FOREIGN KEY (app_id) REFERENCES arr_applications(id) ON DELETE CASCADE
);
