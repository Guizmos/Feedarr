-- Colonne de marquage pour les releases dont unified_category a changé
-- lors d'un reprocess et dont l'entity_id doit être recalculé.
-- POST /api/maintenance/rebind-entities traite le backlog.
ALTER TABLE releases ADD COLUMN needs_rebind INTEGER NOT NULL DEFAULT 0;

-- Index partiel : seules les lignes à traiter sont indexées (volume faible en prod normale)
CREATE INDEX IF NOT EXISTS idx_releases_needs_rebind
ON releases(id) WHERE needs_rebind = 1;
