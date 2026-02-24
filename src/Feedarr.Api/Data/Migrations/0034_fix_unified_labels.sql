-- Corrige les unified_label incohérents avec unified_key dans source_categories.
-- Cas connu : Torr9 cat 5070 (Anime standard Torznab) avait unified_label='Animation'
-- au lieu de 'Anime', causant 2× "Animation" dans la library.
UPDATE source_categories
SET unified_label = 'Anime'
WHERE unified_key = 'anime' AND unified_label <> 'Anime';

UPDATE source_categories
SET unified_label = 'Animation'
WHERE unified_key = 'animation' AND unified_label <> 'Animation';

UPDATE source_categories
SET unified_label = 'Films'
WHERE unified_key = 'films' AND unified_label <> 'Films';

UPDATE source_categories
SET unified_label = 'Series TV'
WHERE unified_key = 'series' AND unified_label <> 'Series TV';

UPDATE source_categories
SET unified_label = 'Emissions'
WHERE unified_key = 'shows' AND unified_label <> 'Emissions';

UPDATE source_categories
SET unified_label = 'Jeux'
WHERE unified_key = 'games' AND unified_label <> 'Jeux';

UPDATE source_categories
SET unified_label = 'Livres'
WHERE unified_key = 'books' AND unified_label <> 'Livres';

UPDATE source_categories
SET unified_label = 'Comics'
WHERE unified_key = 'comics' AND unified_label <> 'Comics';

UPDATE source_categories
SET unified_label = 'Audio'
WHERE unified_key = 'audio' AND unified_label <> 'Audio';

UPDATE source_categories
SET unified_label = 'Spectacle'
WHERE unified_key = 'spectacle' AND unified_label <> 'Spectacle';
