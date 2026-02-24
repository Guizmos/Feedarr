-- Ajoute cat_id=5070 (sous-catégorie Anime standard Torznab) pour toutes les sources
-- qui ne l'ont pas encore explicitement mappée.
-- Permet à PickBestCategoryId (ProtectedKeys includes "anime") de naturellement
-- préférer 5070→anime sur tout specId générique (ex: 105000→series).
INSERT OR IGNORE INTO source_categories (source_id, cat_id, name, unified_key, unified_label)
SELECT s.id, 5070, 'Anime', 'anime', 'Anime'
FROM sources s
WHERE NOT EXISTS (
    SELECT 1 FROM source_categories sc
    WHERE sc.source_id = s.id AND sc.cat_id = 5070
);
