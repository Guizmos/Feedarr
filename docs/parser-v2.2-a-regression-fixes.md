# Parser V2.2-A — correction des 18 régressions

Correctifs locaux sur `beta`, après la revue de sécurité. Aucun commit ni push. Le rapport initial et la revue sont conservés comme état historique, leurs métriques ne décrivent pas ce nouveau candidat.

## Résultat et périmètre

Les **18 régressions confirmées sont corrigées**, vérifiées dans les sorties du corpus réel et dans 18 tests permanents comparant tous les champs structurés. Les **409 améliorations lexicales** de la revue précédente sont conservées **à l'identique, tous champs confondus**. La présence d'un poster n'est jamais utilisée comme vérité d'identité.

Le même corpus contient 2 000 releases, dont 1 657 avec poster et 343 sans poster. Deux exécutions finales produisent des résultats JSON identiques octet pour octet.

## Causes communes corrigées

- **Groupes :** le séparateur doit suivre une chaîne adjacente de tokens de suffixe, au lieu de dépendre d'un marqueur quelconque plus tôt dans le titre. Les tirets internes aux tokens audio sont exclus. Un suffixe candidat contenant encore une version/build ou une plateforme composée est refusé. Du texte libre inconnu ne suffit plus à inventer un groupe depuis un tiret interne ; les signatures terminales explicitement espacées restent possibles après contexte technique.
- **Vocabulaire partagé :** E-AC-3 et AC-3 sont reconnus atomiquement dans le lexer technique ; LC et NVENC conservent le contexte audio/encodage. Les labels de distribution de jeux servent uniquement de preuves pour les groupes, sans devenir de nouvelles frontières de titre.
- **Versions :** les blocs fermés de version/build sont reconnus avec parenthèses et crochets, y compris après des blocs non reconnus. Aucun fragment de version n'est réintroduit dans le titre.
- **Crochets vidéo :** la frontière englobe l'ouverture du bloc technique reconnu ; le nettoyage ne laisse plus `[BD`.
- **Éditions :** un marqueur court comme Remastered reste dans le préfixe de titre lorsqu'aucune version/build n'établit une distribution. Le comportement des blocs fermés explicitement techniques et des éditions longues est conservé.

Aucune exception par œuvre, modification de matching métier, catégorie, repository ou association manuelle. Les espaces sont normalisés pour la tokenisation des groupes de jeux en préservant les offsets du brut.

## Les 18 cas

Index de la revue exhaustive précédente ; signatures privées anonymisées dans cette synthèse. Les sorties exactes sont dans l'artefact privé `regressions.json`.

| Index | Avant correction | Après correction |
| --- | --- | --- |
| 000 — Salvation | version Ver 1.2.5878 dans TitleClean | version retirée, autres champs conservés |
| 455 — Moonlighter 2 | version V1.0.42.3 dans TitleClean | version retirée, numéro 2 conservé |
| 008 — Gears of War E-Day | groupe Day.ISO.WINDOWS.MULTi-RUNE | groupe RUNE, E-Day reste dans le titre |
| 404 — Rafale-C | groupe C.v1.1.0-NoTag | groupe NoTag, Rafale-C reste dans le titre |
| 189, 215, 313 — audio E-AC-3 | groupe AC-3.5.1-TEAM | groupe terminal TEAM |
| 513 — audio AC-3 | groupe 3-TEAM | groupe terminal TEAM |
| 211, 467 — versions Switch | groupe v22.5.0.Switch-TEAM | groupe terminal TEAM |
| 165, 216, 291, 481, 519, 522 — alias avec tirets | BOOK, CROODS, AGE, SENKi, GUARDiANS ou DASTERiX inventé comme groupe | groupe inconnu ; aucun nouveau groupe déduit d'un alias |
| 448 — Night on the Galactic Railroad | TitleClean terminé par [BD | préfixe du titre conservé sans fragment technique |
| 473 — Call of Duty 4 | Remastered supprimé | Call of Duty 4 Modern Warfare Remastered |

Les alias après la frontière ne sont pas réintroduits arbitrairement dans TitleClean : leur représentation reste dans les ambiguïtés de revue. Corriger un faux groupe n'établit pas la bonne identité provider.

## Les 97 ambiguïtés

Aucune nouvelle référence attendue ni identité provider n'est déclarée validée. Les 97 cas conservent leur statut ambigu ; **96 sorties sont strictement inchangées**.

| Famille | Nombre | Traitement |
| --- | ---: | --- |
| Années d'émission | 8 | Inchangées : année de diffusion/édition versus année d'identité non tranchée |
| Groupes DTS/AC3 retirés | 5 | Inchangés : audio ou signature, aucune décision supplémentaire |
| Alias, salles et texte libre après frontière | 78 | Inchangés : utilité pour l'identité non démontrée |
| Collections, paquets mixtes et signatures ambiguës | 6 | Cinq inchangés ; une abstention sur un groupe partiel |

Le groupe partiel `Pis.mkv`, extrait de `Manneken-Pis`, devient **inconnu** : le moteur refuse la déduction partielle, sans choisir un groupe ou une identité de remplacement. Son cas reste ambigu. Les titres de paquets multi-œuvres, nombres et années d'édition ne sont pas corrigés arbitrairement.

## Comparatif complet

| Comparaison | Releases différentes | Avec poster | Sans poster |
| --- | ---: | ---: | ---: |
| V2.2-A revu → candidat corrigé | 22 | 19 | 3 |
| V2.1 figé → candidat corrigé | 521 | 432 | 89 |

Les 22 changements par rapport au candidat revu comprennent les 18 corrections demandées et quatre changements structurels supplémentaires examinés :

1. Godzilla Destroy All Monsters Melee conserve Remastered dans le titre.
2. DreamWorks Shrek 2 conserve Remastered dans le titre ; le numéro 2 reste présent.
3. Pis.mkv devient inconnu, comme indiqué ci-dessus.
4. GAMES.FRENCH, extrait du milieu de IGG-GAMES dans une release de Battlestar Galactica Scattered Hopes, devient inconnu. Aucune signature de remplacement n'est inventée.

Ces quatre effets ne suppriment aucun mot du titre. Les deux changements Remastered ajoutent un descripteur d'édition explicitement présent ; les deux changements de groupe sont des abstentions conservatrices. Aucune nouvelle suppression importante de titre observée parmi les 1 657 contrôles avec poster.

Par rapport au candidat revu : six TitleClean et seize ReleaseGroup changent ; **Year, Season, Episode, Codec, Source, Resolution, AirDate et MediaType sont identiques sur les 2 000 lignes**. Aucun titre vide. Par rapport à V2.1, aucune année déjà renseignée n'est perdue ou remplacée ; les 345 ajouts d'année restent ceux du lot précédent.

Par rapport à V2.1 : 494 TitleClean, 345 Year, 46 ReleaseGroup et une Source diffèrent. Ces comptes se recouvrent ; ils ne mesurent pas une précision globale.

## Tests et attentes historiques

- 461 tests ciblés passent : parser, fixtures, consommateurs, protections des corrections manuelles et politique de matching ; providers simulés et données temporaires.
- Le nouveau fichier `TitleParserSafetyRegressionTests.cs` contient les 18 cas structurés et 11 garde-fous supplémentaires sur les éditions, groupes et mots inconnus.
- Les 174 entrées historiques restent présentes. Deux cellules attendues diffèrent du Git de départ : le groupe composé Tsundere-Raws.CR (déjà corrigé dans le lot local précédent), et désormais TitleClean de `Game.Remastered-CODEX`, `Game` → `Game Remastered`. Cette seconde correction est nécessaire pour ne pas encoder la suppression abusive d'une édition dans le test. Les 172 autres instantanés sont inchangés.
- Une attente ajoutée pendant V2.2-A pour UnknownDistribution-TEAM est corrigée : groupe inconnu, car une signature après un mot libre non reconnu ne doit pas être considérée prouvée. Le titre et ses caractéristiques techniques restent inchangés.
- Build Release réussi, zéro avertissement, zéro erreur ; `git diff --check` propre.
- Suite backend complète exécutée **une seule fois** : **1 697 tests réussis**, zéro échec, zéro ignoré, 56 secondes.

## Performance comparable

Même .NET 8.0.33 Arm64, même corpus, cinq itérations, deux échauffements, 10 000 appels mesurés ; compilation exclue.

| Mesure | V2.1 figé | V2.2-A avant correction | Corrigé A | Corrigé B |
| --- | ---: | ---: | ---: | ---: |
| Médiane (µs) | 92,6 | 47,0 | 50,1 | 50,1 |
| p95 (µs) | 223,1 | 98,1 | 104,5 | 106,0 |
| Somme parsing (ms) | 1 060,77 | 537,89 | 568,98 | 572,75 |

Le coût du contrôle structurel supplémentaire est mesuré ; l'essentiel du gain V2.2-A reste conservé. Ces mesures locales ne garantissent ni la latence de production ni une précision d'identité.

## Artefacts et intégrité

Détails privés ignorés par Git sous `tools/parser-benchmark/local/` :

- `v22a-corrected-review/summary.json` : assertions des 18 corrections, des 409 sorties inchangées et des 97 ambiguïtés.
- `v22a-corrected-review/regressions.json` et `review.md` : brut et sorties complètes avant/après.
- `v22a-corrected-review/ambiguities.json` : classement maintenu, motifs et champs changés.
- `v22a-corrected-review/from-reviewed-v22a.json` et `from-v21.json` : comparaisons structurées exhaustives des deux bases.
- `v22a-corrected-8/` et `v22a-corrected-verified/` : résultats finaux reproductibles et timings.
- `v22a-fix-targeted-final.log`, `v22a-fix-release.log`, `v22a-fix-backend-full-once.log` : validations.

Corpus canonique : `04293b3ed1490b4bb0f62926d7e2570abe9f0cfe9c77a9788e982b61621f7538`. Source finale : `748679ff8396f532760034ceabcd45b94bb1b227471238769410cb39ea7a106f`.

La base de référence n'a pas été ouverte : utilisation exclusive du corpus JSON existant. SHA-256 toujours `23a3d0a355958f2eec8ed562c524282c5b5259a5ff70060991e23bedda7b159d` ; aucun WAL/SHM/journal créé. Aucun accès NAS, Docker, provider, migration, reparse de données persistées ou écriture SQLite. Les modifications étrangères et les références initiales sont conservées.

Fichiers touchés pendant cette correction : `TitleParser.Suffix.cs`, `TitleParserSuffixSequenceTests.cs`, nouveau `TitleParserSafetyRegressionTests.cs`, une cellule du golden et ce rapport. Aucun commit ni push : les différences restent disponibles pour examen.
