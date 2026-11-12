# Parser V2.2-B — composants communs et spécialisation incrémentale

Implémentation locale sur `beta`, après `332984b`. Aucun commit/push automatique.

## Architecture livrée

`TitleParser.Parse()` reste la façade publique ; `ParsedTitle` conserve son namespace, son contrat et son comportement mutable par appel.

- `TitleParser.Suffix.cs` : composant `TechnicalRecognition`, propriétaire de la tokenisation et de l'analyse technique commune (formats, sources, codecs, langues, groupes et frontières).
- `TitleParser.Vocabulary.cs` : catalogue unique des regex existantes, déplacées sans ajouter de règles ou d'exceptions par œuvre.
- `TitleParser.Categories.cs` : entrée immuable par appel et politiques stateless partagées. Films, Animation et Spectacles partagent la politique vidéo ; Série/Anime partagent la structure épisodique ; Émission conserve sa priorité aux dates.
- `TitleParser.Episodes.cs` : extraction réutilisable des saisons, épisodes et dates de diffusion, sans duplication entre Série, Anime et Émission.
- `TitleParser.Game.cs` : première spécialisation complète de politique, propriétaire de la normalisation PC, des règles d'année des versions/builds/éditions annuelles et du nettoyage du titre de jeu. Le chemin jeu ne traverse plus la recherche d'épisodes ou les règles de packs/éditions vidéo.
- `TitleParser.Context.cs` et `.Text.cs` : preuves lexicales d'année et opérations de texte communes ; politique vidéo conservée et réutilisée.

Il n'y a pas sept moteurs indépendants. Le catalogue et l'analyse technique restent communs ; les spécialisations ne divergent que là où les contrats existants diffèrent. La sélection de politique ne change ni la catégorie d'entrée ni MediaType. Animation n'hérite pas implicitement de la sémantique Anime.

## Vérification des sorties

Après extraction des composants communs, puis après spécialisation et sur le candidat final : **aucune différence de sortie structurée sur les mêmes 2 000 releases**, y compris les 1 657 contrôles avec poster et les 343 sans poster. Les dix champs ParsedTitle sont identiques à V2.2-A.

Les 174 fixtures et leurs attentes sont inchangées. Les 18 corrections et 409 améliorations V2.2-A restent préservées ; les ambiguïtés ne sont ni reclassées ni transformées en références validées. Aucune précision globale ou identité provider n'est déduite du benchmark.

Corpus canonique SHA-256 : `04293b3ed1490b4bb0f62926d7e2570abe9f0cfe9c77a9788e982b61621f7538`.
Source finale : `e124cfb6c9c11ebe53eb21f9e8c0a913a80736bf0bc1324174238b80617ef4b5`.

## Tests et performances

- 474 tests ciblés réussis, dont 13 nouveaux tests de contrats par catégorie et d'isolation des politiques lors de parsings concurrents sur la même façade.
- Build Release réussi : zéro avertissement, zéro erreur.
- Suite backend complète exécutée **une seule fois** en fin de lot : **1 710 réussites**, zéro échec, zéro ignoré (57 s). Log privé : `local/v22b-backend-full-once.log`.
- `git diff --check` propre.

Même .NET 8.0.33 Arm64, cinq itérations, deux échauffements et 10 000 appels sur le corpus fixe. V2.2-A a été remesuré depuis un instantané de ses sources dans le même environnement.

| Mesure | V2.2-A remesuré | V2.2-B final |
| --- | ---: | ---: |
| Médiane par appel (µs) | 51,2 | 50,7 |
| p95 (µs) | 107,1 | 105,1 |
| Somme parsing (ms) | 579,69 | 574,71 |

Les mesures sont comparables et ne montrent pas de régression matérielle ; ces petits écarts ne constituent pas une preuve d'accélération. L'objectif de ce lot est structurel, sans changement de comportement.

## Sécurité et artefacts

Aucun matching, ID externe, correction manuelle, migration ou autre comportement métier modifié. Aucun provider externe, accès NAS/Docker/production ni reparse de données persistées. La base n'est pas ouverte ; seul le corpus JSON existant est utilisé. SHA-256 SQLite inchangé : `23a3d0a355958f2eec8ed562c524282c5b5259a5ff70060991e23bedda7b159d`, aucun sidecar créé.

Artefacts privés sous `tools/parser-benchmark/local/`, toujours ignorés : `v22b-common/`, `v22b-specialized/`, `v22b-final/` et son `comparison.json`, `v22b-baseline-remeasured/`, ainsi que les logs ciblés/Release/backend. Aucun export privé dans les fichiers versionnables.

Les fichiers étrangers `.artifacts/` et `feedarr-poster-storage-audit.py` sont préservés. Aucun commit ni push effectué.
