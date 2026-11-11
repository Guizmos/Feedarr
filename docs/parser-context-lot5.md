# Lot 5 — parsing conservateur des titres et années

Base : `beta` au commit `32bd510`. Périmètre : MOT-P2-01 et MOT-P2-02.
Les reproductions de l’audit métier existant sont réutilisées : `The.French.Dispatch`
perdait « French », `The.English.Patient` perdait « English », `The.Collection`
perdait « Collection » et `Cyberpunk.2077` perdait son nombre tout en exposant 2077
comme année. Aucun nouvel audit général, accès au NAS ou reparse de données réelles.

## Causes confirmées

Les expressions de langue, collection, édition et bruit étaient appliquées au titre
entier après nettoyage. La position et la fonction d’un mot n’étaient pas prises en
compte. Côté jeux, `RxGameYear` effaçait tout nombre ressemblant à une année ; les
versions nues et groupes après tiret pouvaient aussi supprimer des morceaux de titre.
L’extraction d’année choisissait essentiellement le dernier nombre de quatre chiffres,
sans preuve qu’il s’agissait d’une date. Les dates entre parenthèses étaient au
contraire perdues par le pré-nettoyage avant même cette extraction.

## Règles retenues

- Séparer un préfixe de titre d’un suffixe technique identifié : format vidéo,
  saison/épisode, date d’émission, version/build, plateforme ou groupe de release.
- Conserver les mots de langue/nationalité/collection dans le titre. Les tags sont
  retirés dans le suffixe, avec des indices supplémentaires pour les mots ambigus.
  Un mot comme English ou Collection ne disparaît plus simplement parce qu’il est
  dans une liste. Les mots de plateforme intégrés à un titre sont aussi préservés.
- Ne plus effacer arbitrairement les contenus entre crochets/parenthèses. Conserver
  les sous-titres non techniques ; reconnaître les labels de fansub et les suffixes
  explicitement techniques. Ne pas interpréter un tiret naturel comme groupe sans
  contexte de release.
- Distinguer le nombre candidat de l’année retenue. Une date isolée entre parenthèses
  ou crochets est explicite. Un nombre initial reste un titre. Pour la vidéo, une
  date précédant un suffixe technique fournit le contexte de sortie. Avec plusieurs
  nombres, conserver le numéro d’œuvre et retirer seulement l’année retenue.
- Pour les jeux, un nombre isolé avant les tags n’est pas une date certaine, quelle
  que soit sa valeur passée ou future. Le conserver dans le titre, avec Year=NULL.
  Une seconde année distincte, une date explicitement délimitée ou une date dans une
  région déjà identifiée comme métadonnées peut être extraite. Les composants de
  versions et builds ne deviennent pas des années.
- Les plages de dates de collections confirmées ne représentent pas une année unique.
  Les plages d’éditions de jeux sont conservées. Un nombre de titre de série juste
  avant SxxExx reste conservé s’il n’y a pas d’autre preuve de date.
- Préserver le contrat public ParsedTitle et la correspondance des catégories réelles
  Feedarr. Les marqueurs saison/épisode explicites sont aussi lus pour Anime. Les
  épisodes multiples conservent leur premier épisode, comme auparavant.

Aucune règle ne contient le nom d’une œuvre. Pas de bannissement général des années
futures : une date explicitement marquée en 2035 est reconnue, tandis qu’un numéro
annuel de jeu de 2000 peut rester dans le titre s’il est ambigu.

## Exemples

| Entrée et catégorie | Résultat | Année |
| --- | --- | --- |
| The.French.Dispatch — Film | The French Dispatch | NULL |
| The.English.Patient.1996.MULTI.VFF.1080p — Film | The English Patient | 1996 |
| The.Collection.2012.1080p.WEB-DL — Film | The Collection | 2012 |
| Cyberpunk.2077.PC-GOG — JeuWindows | Cyberpunk 2077 | NULL |
| Cyberpunk.2077.v2.1.MULTI-GRP — JeuWindows | Cyberpunk 2077 | NULL |
| Cyberpunk.2077.2020.v2.1.PC-GOG — JeuWindows | Cyberpunk 2077 | 2020 |
| FIFA.2026 / F1.2026 — JeuWindows | FIFA 2026 / F1 2026 | NULL |
| Blade.Runner.2049.2017.1080p — Film | Blade Runner 2049 | 2017 |
| Half-Life — JeuWindows | Half-Life | NULL |
| Le.French.Show.10.11.2026.1080p — Emission | Le French Show | 2026, AirDate=2026-11-10 |

### Attentes anciennes corrigées explicitement

Trois anciens tests de jeux supprimaient une année non balisée sans preuve :
`Mewgenics.2026.MULTI...`, `Civilization.VII.2025.MULTI...` et
`Lapin.Malin.Maternelle.3.2000.FRENCH...`. Ces entrées gardent désormais respectivement
2026, 2025 et 2000 dans le titre ; les tags/versions sont bien retirés. Il est impossible
de distinguer ces formes d’une édition annuelle telle que FIFA.2026 en se fondant
uniquement sur cette position du nombre, sans exception propre à l’œuvre.

Cette évolution est volontaire : une date de sortie non balisée peut rester dans
une requête de jeu, plutôt que perdre irréversiblement un numéro faisant partie du
nom. Des variantes avec `(2026)`, `(2025)` et `(2000)` vérifient en parallèle que les
dates explicites restent reconnues et retirées. Les trois attentes ont été mises à
jour, sans masquer les autres régressions détectées pendant le développement.

## Données et périmètre

Aucune migration SQLite. Aucun changement aux repositories, matching, métadonnées,
Backup V2, wizard, GC ou frontend. Aucun travail automatique sur les titres originaux
stockés, caches, posters ou entities. Le comportement s’applique aux nouveaux appels
au parser et aux reparsings explicitement demandés. Les protections existantes des
corrections manuelles restent inchangées et sont vérifiées par les tests consommateurs.
Les fichiers locaux étrangers et les rapports de validation existants sont préservés.

## Validation et fichiers

- Suite backend complète exécutée une seule fois : 1311 réussites, aucun échec ni test
  ignoré. Rapport TRX dans `.artifacts/lot5-validation/` (non versionné).
- Après cette suite, un dernier garde-fou conserve Complete/Intégrale dans un titre
  de série sans preuve de pack. Les 187 tests ciblés passent sur cet état final :
  parsing existant et nouveau, consommateurs repositories, corrections manuelles,
  posters et stratégies de matching. La suite complète n’a pas été relancée.
- Compilation Release finale : zéro avertissement, zéro erreur.
- `git diff --check` propre ; aucun changement frontend, aucun test frontend lancé.
- `origin/beta` vérifié à jour avec la base avant livraison.

Fichiers du lot :

- `src/Feedarr.Api/Services/Title/TitleParser.cs`
- `src/Feedarr.Api/Services/Title/TitleParser.Context.cs`
- `src/Feedarr.Api.Tests/TitleParserContextTests.cs`
- `src/Feedarr.Api.Tests/TitleParserGameParsingTests.cs`
- `docs/parser-context-lot5.md`
