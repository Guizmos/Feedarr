-- Explicit, conservative migration. Never rewrite app_settings here: its exact
-- content is the baseline of pending configuration restores and session revocation.
ALTER TABLE web_sessions ADD COLUMN secure_only INTEGER NOT NULL DEFAULT 1 CHECK(secure_only IN(0,1));
CREATE TABLE security_transport_migration (
    id INTEGER PRIMARY KEY CHECK(id=1),
    legacy_mode TEXT NOT NULL,
    require_https INTEGER NOT NULL CHECK(require_https IN(0,1))
);
INSERT INTO security_transport_migration(id,legacy_mode,require_https)
SELECT 1,COALESCE(json_extract(value_json,'$.authMode'),json_extract(value_json,'$.authentication'),'protected'),
 CASE WHEN json_type(value_json,'$.requireHttps') IS NOT NULL THEN CAST(json_extract(value_json,'$.requireHttps') AS INTEGER)
      WHEN lower(COALESCE(json_extract(value_json,'$.authMode'),CASE WHEN json_extract(value_json,'$.authentication')='none' THEN 'open' ELSE 'strict' END))='open' THEN 0 ELSE 1 END
FROM app_settings WHERE key='security';
-- Existing Open / incomplete instances need an owner-authorized administrator.
UPDATE instance_initialization SET initialized=0 WHERE NOT EXISTS(
 SELECT 1 FROM app_settings WHERE key='security'
 AND length(trim(COALESCE(json_extract(value_json,'$.username'),'')))>0
 AND length(COALESCE(json_extract(value_json,'$.passwordHash'),''))>0
 AND length(COALESCE(json_extract(value_json,'$.passwordSalt'),''))>0
);
