# Parser V2.2-A — correctifs et revue avant intégration

État : correctifs locaux sur `beta`, base Git `294eacb`. Aucun commit ni push. Les différences du corpus réel doivent être examinées avant intégration.

## Causes et correctifs

L'ancienne validation exigeait que toute la fin du nom soit reconnue : une signature composée ou un mot inconnu après plusieurs marqueurs techniques annulait la frontière déjà établie. Les chemins vidéo/jeu, l'année, le groupe et les caractéristiques ne partageaient pas une reconnaissance suffisamment cohérente.

Une analyse en séquence typée partage maintenant les catalogues source/codec et les marqueurs techniques. Deux indices techniques adjacents peuvent établir une frontière malgré une signature inconnue ultérieure. Les mots inconnus avant ces preuves restent une barrière. Les tokens faibles (SD, AD, PCM, services) exigent un contexte ; ils ne sont pas supprimés aveuglément des titres.

Les versions/builds, plateformes et distributions de jeux (dont PREACTIVED) sont reconnus avec leurs blocs parenthésés/crochetés. Les numéros de titres et les mots d'édition restent protégés. Les groupes composés conservent leur préfixe ; les tirets internes à WEB-DL ou DTS-HD ne commencent pas un groupe.

VFF, SD, AD, HDR10Plus, PCM et les codes de service contextualisés participent à la même analyse. Aucun nom d'œuvre n'est codé en dur. L'année est calculée une fois par parsing ; les index des groupes regex sont mis en cache par vocabulaire, sans cache global des titres.

## Références

Les 120 cas de revue initiaux sont conservés. Dix-huit sont validés uniquement pour **TitleClean et Year**, sur des preuves lexicales de suffixe ; 102 restent des propositions à valider. Aucune identité provider n'est validée, et la présence d'un poster n'est pas une référence.

Sur ces 18 références partielles : conformité complète des deux champs de 0 à 18 cas, 33 contraintes de champs améliorées et aucune régression. Ce sous-ensemble ciblé ne mesure pas la précision globale.

Exemples de structure, signatures privées anonymisées :

| Entrée | Avant | Après attendu lexicalement |
| --- | --- | --- |
| Miyazaki.Ponyo.Sur.La.Falaise.2008.FRENCH.BRRip.x264.AC3-TEAM.ONE | Suffixe technique conservé dans le titre, année absente | Miyazaki Ponyo Sur La Falaise ; 2008 |
| Bodycam.v0.8.12.19346.WIN.X64.MULTI.PORTABLE.PREACTIVED-TEAM | Version et distribution conservées | Bodycam ; année absente |
| La.Belle.et.l.Empereur.1959.VFF.SD.DVDRip.AAC.2.0.x264-TEAM | Suffixe conservé, année absente | La Belle et l Empereur ; 1959 |

Les tests permanents utilisent des signatures synthétiques de même structure. Ils protègent aussi French, AD, IT, PCM, Origin, Portal 2 et Cyberpunk 2077.

## Comparaison exacte du corpus réel

Même corpus déterministe de 2 000 releases : 343 sans poster et 1 657 avec poster. SHA-256 canonique : `04293b3ed1490b4bb0f62926d7e2570abe9f0cfe9c77a9788e982b61621f7538`.

Baseline figée avant modification : source `cbe4fa55015cfa39e48befdd68574f17cce043001be34c30b554502914a0a79c`. Candidat final : `b67405857509a71539c2b4815a51e5c09c23fc5e3e3403c16813af6b1620e74d`. Deux exécutions finales donnent des résultats structurés identiques octet pour octet.

524 releases changent (91 sans poster, 433 contrôles avec poster) ; 1 476 restent identiques. Les comptes de champs ci-dessous se recouvrent.

| Champ modifié | Sans poster | Avec poster | Total |
| --- | ---: | ---: | ---: |
| TitleClean | 86 | 408 | 494 |
| Year | 62 | 283 | 345 |
| ReleaseGroup | 15 | 44 | 59 |
| Source | 0 | 1 | 1 |
| Season, Episode, Codec | 0 | 0 | 0 |
| Resolution, AirDate, MediaType | 0 | 0 | 0 |

Les 345 années sont exclusivement des ajouts depuis une valeur absente : aucune année renseignée perdue ou remplacée. Répartition : Film 185, Animation 125, Spectacle 25, Série 2, Émission 8. Source : une graphie Blu-rayRip est maintenant reconnue BluRay.

Groupes : 24 signatures composées étendues, 30 groupes auparavant absents reconnus, cinq anciennes valeurs DTS/AC3 devenues absentes car reconnues comme audio. Ces différences doivent rester visibles lors de la revue.

Les signalements heuristiques évoluent de 352 à 0 pour les suffixes vidéo techniques, 73 à 30 pour les jeux, 449 à 45 pour les langues, 72 à 53 pour les collections ; les 12 marqueurs de série résiduels ne changent pas. Ce sont des statistiques descriptives, **pas une mesure de précision**.

## Changements sensibles restant à valider

- Huit Émissions gagnent une année selon la règle générique existante : année d'édition annuelle, de diffusion et d'identité restent à distinguer. Aucun changement de règle métier Émission n'a été introduit.
- Les cinq groupes DTS/AC3 retirés restent ambigus si un groupe porte réellement un nom audio ; les autres signatures composées doivent également être revues.
- Du texte libre après une séquence technique établie est retiré : cela peut contenir un titre alternatif ou une salle de spectacle. Exemple anonymisé : `Léo.Delibes.Lakmé.2011.720p.MKV.flac.2.0.(Sydney.Opéra)` devient `Léo Delibes Lakmé`, année 2011. La pertinence pour l'identité provider n'est pas validée.
- Les éditions, packs multi-œuvres et distributions console restent des propositions ; aucune reclassification métier n'est appliquée. Les priorités existantes date/épisode et Animation/Anime sont inchangées.
- Une qualité de remaster peut encore être confondue avec la qualité du fichier : comportement existant conservé, à traiter séparément.

La politique conservatrice d'année est conservée : `Blade.Runner.2049.1080p` garde 2049 dans le titre avec année inconnue ; une seconde année explicite reste exploitable. Le cas résolution seule est également protégé pour 576p.

## Tests et performance

- 432 tests ciblés réussis, dont les 174 fixtures permanentes et 56 nouveaux cas de suffixes ; contrôles consommateurs, corrections manuelles et matching inclus avec SQLite temporaire/HTTP simulé.
- Une seule cellule attendue des 174 fixtures est corrigée : groupe `Raws.CR` → `Tsundere-Raws.CR`, restitution du préfixe composé. Les 173 autres instantanés sont inchangés.
- 22 tests des outils benchmark réussis.
- Compilation Release réussie : zéro avertissement, zéro erreur. Aucune suite backend complète lancée dans ce lot ciblé.

Mesures comparables : .NET 8.0.33 Arm64, même corpus, cinq itérations et deux échauffements, 10 000 appels mesurés. Temps de parsing hors compilation.

| Mesure | Avant | Après A | Après B |
| --- | ---: | ---: | ---: |
| Médiane par appel (µs) | 92,6 | 51,0 | 47,0 |
| p95 par appel (µs) | 223,1 | 109,4 | 98,1 |
| Somme des appels (ms) | 1 060,77 | 591,84 | 537,89 |

La suppression des validations répétées, la réutilisation de l'analyse par appel et des index de groupes réduisent le coût. Les variations A/B rappellent qu'il s'agit de mesures locales, sans garantie de latence en production.

## Artefacts et sécurité

Tous les détails privés restent sous `tools/parser-benchmark/local/`, ignoré par Git :

- `v22a-before/` : résultats/performance figés avant correctifs.
- `v22a-final-checked-a/` et `v22a-final-checked-b/` : résultats finaux reproductibles.
- `v22a-review-diff/comparison.json` : différences structurées par release et champ ; `comparison.md` : synthèse.
- `v22a-references.json` : 18 références lexicales partielles et statuts des 120 cas.
- `v22a-review-proposals.json` : avant/candidat et propositions des 102 cas non validés, identité provider explicitement non revue.
- `v22a-targeted-final.log`, `v22a-tool-tests.log`, `v22a-release-final.log` : validations.

La base locale n'a pas été ouverte par le benchmark : les exports existants suffisent. SHA-256 final identique à l'initial : `23a3d0a355958f2eec8ed562c524282c5b5259a5ff70060991e23bedda7b159d`, permissions 0700, aucun fichier WAL/SHM/journal créé. Aucun accès NAS, Docker, provider, aucune migration, association, correction manuelle ni donnée persistée modifiée.

Fichiers de ce lot : `TitleParser.cs`, `TitleParser.Context.cs`, nouveau `TitleParser.Suffix.cs`, nouveau `TitleParserSuffixSequenceTests.cs`, la cellule de groupe du golden et ce rapport. Les modifications locales antérieures des outils V2.1, du rapport V2.1 et les autres fichiers étrangers sont conservées.
