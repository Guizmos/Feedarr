-- ─────────────────────────────────────────────────────────────────────────────
-- Migration 0043 : Performance indexes lot 5 — Stats endpoints
-- Cible: StatsIndexers (GROUP BY temp b-tree) + StatsProviders (full scan I/O)
-- ─────────────────────────────────────────────────────────────────────────────

-- Index 1: Expression covering index pour StatsIndexers GROUP BY
--
-- La requête fait:
--   FROM releases r JOIN sources s ON r.source_id = s.id WHERE s.enabled = 1
--   GROUP BY s.id, s.name, COALESCE(NULLIF(TRIM(r.unified_category),''),'Autre')
--
-- Sans cet index: USE TEMP B-TREE FOR GROUP BY (tri externe sur expression).
-- Avec cet index: SQLite lit releases en ordre (source_id, expr) → streaming
-- GROUP BY sans allocation de b-tree temporaire.
-- La 3e colonne (category_id) rend l'index couvrant pour MAX(category_id).
CREATE INDEX IF NOT EXISTS idx_releases_source_unified_expr
ON releases(
    source_id,
    COALESCE(NULLIF(TRIM(unified_category),''),'Autre'),
    category_id
);

-- Index 2: Covering index pour StatsProviders subquery
--
-- La subquery fait:
--   SELECT LOWER(TRIM(COALESCE(NULLIF(me.ext_provider,''),
--                               NULLIF(r.ext_provider,''),
--                               NULLIF(r.poster_provider,'')))) as providerKey
--   FROM releases r LEFT JOIN media_entities me ON me.id = r.entity_id
--
-- Sans cet index: SCAN TABLE releases — lit les lignes complètes (40+ colonnes)
-- alors que seules 3 colonnes sont utilisées.
-- Avec cet index: SQLite scanne l'index compact (3 colonnes) au lieu des pages
-- de table → réduction significative des I/O sur grosses tables.
CREATE INDEX IF NOT EXISTS idx_releases_provider_lookup
ON releases(entity_id, poster_provider, ext_provider);
