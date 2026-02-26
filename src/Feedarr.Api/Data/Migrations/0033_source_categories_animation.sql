-- Corrige le mapping des catégories d'animation occidentale :
-- C411  : cat_id=102060 "Animation Films & Vidéo-clips" anime → animation
-- Ygégé : cat_id=102178 "Film/Vidéo : Animation"       anime → animation
UPDATE source_categories
SET unified_key = 'animation', unified_label = 'Animation'
WHERE cat_id IN (102060, 102178)
  AND unified_key = 'anime';
