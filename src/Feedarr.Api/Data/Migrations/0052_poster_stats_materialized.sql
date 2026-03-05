CREATE TABLE IF NOT EXISTS poster_stats (
  singleton_id            INTEGER PRIMARY KEY CHECK (singleton_id = 1),
  total                   INTEGER NOT NULL DEFAULT 0,
  missing                 INTEGER NOT NULL DEFAULT 0,
  failed                  INTEGER NOT NULL DEFAULT 0,
  ok                      INTEGER NOT NULL DEFAULT 0,
  missing_actionable      INTEGER NOT NULL DEFAULT 0,
  last_poster_change_ts   INTEGER NOT NULL DEFAULT 0,
  updated_at_ts           INTEGER NOT NULL DEFAULT 0
);

INSERT OR IGNORE INTO poster_stats (
  singleton_id,
  total,
  missing,
  failed,
  ok,
  missing_actionable,
  last_poster_change_ts,
  updated_at_ts
)
VALUES (1, 0, 0, 0, 0, 0, 0, 0);
