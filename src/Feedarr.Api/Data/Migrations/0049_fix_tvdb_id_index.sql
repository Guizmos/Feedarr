-- Remplace l'index non-partiel idx_releases_tvdb_id (créé par 0005) par un index partiel.
-- Les migrations 0026/0047 ont tenté CREATE INDEX IF NOT EXISTS avec WHERE tvdb_id IS NOT NULL,
-- mais le nom étant déjà pris par 0005, l'opération a été silencieusement ignorée.
-- On drop puis recrée pour garantir l'index partiel correct.

DROP INDEX IF EXISTS idx_releases_tvdb_id;

CREATE INDEX IF NOT EXISTS idx_releases_tvdb_id
  ON releases(tvdb_id)
  WHERE tvdb_id IS NOT NULL;
