CREATE TABLE IF NOT EXISTS external_provider_instances (
  instance_id TEXT PRIMARY KEY,
  provider_key TEXT NOT NULL,
  display_name TEXT NULL,
  enabled INTEGER NOT NULL DEFAULT 1,
  base_url TEXT NULL,
  auth_json TEXT NOT NULL DEFAULT '{}',
  options_json TEXT NOT NULL DEFAULT '{}',
  created_at_ts INTEGER NOT NULL,
  updated_at_ts INTEGER NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_external_provider_instances_provider_key
  ON external_provider_instances(provider_key);
