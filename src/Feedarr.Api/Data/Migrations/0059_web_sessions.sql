-- Persistent opaque web sessions. Credential storage is unchanged.
CREATE TABLE web_security_generation (
    id INTEGER PRIMARY KEY CHECK (id = 1),
    generation INTEGER NOT NULL
);
INSERT INTO web_security_generation VALUES (1, 1);
CREATE TABLE web_sessions (
    token_hash TEXT PRIMARY KEY,
    generation INTEGER NOT NULL,
    created_ts INTEGER NOT NULL,
    last_seen_ts INTEGER NOT NULL,
    expires_ts INTEGER NOT NULL,
    idle_seconds INTEGER NOT NULL,
    remember_me INTEGER NOT NULL
);
CREATE INDEX idx_web_sessions_expiry ON web_sessions(expires_ts);
-- Atomic invalidation also covers security writes outside SettingsController.
-- Conservatively invalidate on any effective security document change.
CREATE TRIGGER web_security_changed AFTER UPDATE ON app_settings
WHEN (NEW.key = 'security' OR OLD.key = 'security')
 AND (NEW.key IS NOT OLD.key OR NEW.value_json IS NOT OLD.value_json)
BEGIN
    UPDATE web_security_generation SET generation = generation + 1 WHERE id = 1;
    DELETE FROM web_sessions;
END;
CREATE TRIGGER web_security_inserted AFTER INSERT ON app_settings
WHEN NEW.key = 'security'
BEGIN
    UPDATE web_security_generation SET generation = generation + 1 WHERE id = 1;
    DELETE FROM web_sessions;
END;
CREATE TRIGGER web_security_deleted AFTER DELETE ON app_settings
WHEN OLD.key = 'security'
BEGIN
    UPDATE web_security_generation SET generation = generation + 1 WHERE id = 1;
    DELETE FROM web_sessions;
END;
