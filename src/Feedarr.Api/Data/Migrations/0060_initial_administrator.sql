-- Existing installations keep their authentication mode and sessions unchanged.
CREATE TABLE instance_initialization (
    id INTEGER PRIMARY KEY CHECK(id=1),
    initialized INTEGER NOT NULL CHECK(initialized IN (0,1))
);
INSERT INTO instance_initialization(id,initialized)
SELECT 1, CASE WHEN EXISTS(SELECT 1 FROM app_settings WHERE key='security')
    OR EXISTS(SELECT 1 FROM app_settings WHERE key='ui' AND json_valid(value_json) AND json_extract(value_json,'$.onboardingDone')=1)
    OR EXISTS(SELECT 1 FROM sources) OR EXISTS(SELECT 1 FROM providers)
    OR EXISTS(SELECT 1 FROM arr_applications) OR EXISTS(SELECT 1 FROM configuration_restore_operations)
    THEN 1 ELSE 0 END;
-- Restoring a configuration or using the existing security API also closes initialization.
CREATE TRIGGER initial_security_inserted AFTER INSERT ON app_settings WHEN NEW.key='security'
BEGIN UPDATE instance_initialization SET initialized=1 WHERE id=1; END;
CREATE TRIGGER initial_security_updated AFTER UPDATE ON app_settings WHEN NEW.key='security'
BEGIN UPDATE instance_initialization SET initialized=1 WHERE id=1; END;
