-- Supprime les catégories Torznab "parent round-thousand" (ex : 5000=TV, 7000=Books, 3000=Audio)
-- de source_categories quand des catégories enfants plus spécifiques avec la même unified_key
-- existent déjà pour la même source.
--
-- Problème résolu :
--   Quand 5000 (TV parent) ET 117804 (Séries TV) sont tous deux dans source_categories pour
--   la même source, le sync acceptait TOUS les items TV (y compris ceux dont la sous-cat
--   spécifique comme 102581 n'était pas sélectionnée) car ids.Any(selectedSet.Contains)
--   matchait via 5000.
--
-- Après cette migration, seules les catégories "feuilles" (non-parentes X000) restent.
-- La migration est idempotente (peut être rejouée sans effet si déjà appliquée).

DELETE FROM source_categories
WHERE id IN (
    SELECT sc.id
    FROM source_categories sc
    WHERE sc.cat_id >= 1000
      AND sc.cat_id <= 8999
      AND (sc.cat_id % 1000) = 0
      AND EXISTS (
          SELECT 1 FROM source_categories sc2
          WHERE sc2.source_id = sc.source_id
            AND sc2.unified_key = sc.unified_key
            AND sc2.cat_id > 0
            AND NOT (sc2.cat_id >= 1000 AND sc2.cat_id <= 8999 AND (sc2.cat_id % 1000) = 0)
      )
);
