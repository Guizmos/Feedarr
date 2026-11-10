-- Control-plane data only; no changes to library tables or their foreign keys.
CREATE TABLE IF NOT EXISTS configuration_restore_operations (
    id TEXT PRIMARY KEY,
    applied_at_ts INTEGER NOT NULL,
    initial_sync_state TEXT NOT NULL CHECK(initial_sync_state IN ('pending','held','running','done')),
    source_ids_json TEXT NOT NULL,
    completed_ids_json TEXT NOT NULL DEFAULT '[]',
    next_attempt_ts INTEGER NOT NULL DEFAULT 0,
    attempts INTEGER NOT NULL DEFAULT 0,
    retention_per_category INTEGER,
    retention_global INTEGER
);
