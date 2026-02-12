CREATE TABLE IF NOT EXISTS activity_log (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  source_id INTEGER,
  level TEXT NOT NULL,        -- info/warn/error
  event_type TEXT NOT NULL,   -- sync
  message TEXT,
  data_json TEXT,
  created_at_ts INTEGER NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_activity_created ON activity_log(created_at_ts DESC);
CREATE INDEX IF NOT EXISTS idx_activity_source ON activity_log(source_id, created_at_ts DESC);
