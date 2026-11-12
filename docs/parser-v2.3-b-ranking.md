# V2.3-B — classement isolé des candidats

Implémentation locale sur `beta` après `30a260b`, sans commit/push. Le générateur V2.3 et ses 26 tests restent inchangés. Aucun branchement dans le matching de production.

## Composant

`SearchCandidateRanker` consomme les requêtes du générateur, un contexte immuable et des résultats provider déjà fournis. Il ne fait aucun IO et ne lit pas de verrou ou d'identité en base : le futur adaptateur devra transmettre ces états persistés fiables.

Chaque évaluation expose l'identité `(provider, type, ID)`, l'index de requête, un score heuristique borné 0–1, des raisons positives, des contradictions fortes/faibles et une décision Accept/Ambiguous/Reject. Le résultat global choisit une identité seulement en cas d'acceptation unique suffisamment séparée. Le score n'est pas une probabilité calibrée.

- Normalisation stricte existante, conservant nombres et mots d'édition. Comparaison au titre principal et aux alias explicitement connus du résultat ; ordre des mots et répétitions participent au score.
- Les années ConfirmedYear du contexte, Parsed des requêtes et Hypothesis restent distinctes. Par défaut, tolérance 0 pour l'année confirmée, 1 pour l'année parsée. Une année hypothétique n'obtient aucun bonus d'année confirmée et ne suffit jamais seule à accepter.
- Les incohérences de type, catégorie connue, année fiable/parsée hors tolérance, numéros de titre, saison/épisode ou identité externe comparable sont bloquantes, même avec un score élevé. Métadonnées requises manquantes : ambiguïté.
- Les titres uniquement numériques et titres approchants restent ambigus sans identité fiable indépendante, par défaut. Un alias connu exactement égal constitue une égalité de titre.
- Une identité établie, un verrou ou une correction manuelle retourne PreserveExisting=true sans sélection ni réévaluation : aucune association fiable de remplacement n'est proposée.
- Une indication d'identité fiable mais non établie est une contrainte distincte : les namespaces provider/type doivent correspondre, ou un lien externe connu doit explicitement relier les résultats. Même ID numérique sur un autre provider/type ne constitue pas une preuve.
- Les doublons d'une même identité ne votent pas et ne créent pas de compétition artificielle. Des métadonnées contradictoires pour la même identité empêchent l'acceptation. Les identités distinctes proches déclenchent une abstention globale.
- Aucune donnée poster dans les entrées : sa présence ne peut pas augmenter le score.

Les seuils internes sont configurables et testés : similarité minimale 0,90, confiance d'acceptation 0,92, écart minimal entre identités 0,08 ; tolérances d'année, exigence d'égalité du titre et protection des titres numériques sont également configurables. Les poids du score sont documentés dans le code. Les contradictions et exigences de preuve priment sur le score.

Les entrées sont bornées à deux requêtes et, par défaut, 100 résultats. Un lot trop grand est refusé, jamais tronqué silencieusement au risque de cacher un concurrent. Les sorties et listes de preuves sont en lecture seule.

## Comparaison sur fixtures synthétiques

Ces fixtures vérifient les décisions attendues de la politique ; elles ne mesurent pas une précision sur des identités provider réelles.

| Fixture | Décision par défaut | Motif principal |
| --- | --- | --- |
| Titre exact + année cohérente | Accepter | titre, type et année concordants |
| Alias connu exact + année cohérente | Accepter | alias connu concordant |
| Année parsée 2020 / résultat 1990 | Rejeter | contradiction d'année |
| Année confirmée 2020 / résultat 2021 | Rejeter | tolérance confirmée stricte |
| Année parsée 2020 / résultat 2021 | Accepter | écart contextualisé de 1 an |
| Mauvaise catégorie connue | Rejeter | category-mismatch |
| Film demandé / série retournée | Rejeter | media-type-mismatch |
| Deux identités aux mêmes preuves | Ambigu | close-competing-identities |
| Titre numérique 1917 sans ID fiable | Ambigu | preuve d'identité indépendante requise |
| Film 2 / Film 3 | Rejeter | numéros de titre contradictoires |
| Hypothèse 2049, année provider identique | Ambigu | hypothèse seule insuffisante |
| Hypothèse 2049, année provider 1982 | Ambigu | hypothèse contredite, sans promotion en preuve |
| Man Bites Dog / Dog Bites Man | Rejeter | l'ordre des mots n'est pas une égalité |
| Année provider manquante | Ambigu | preuve attendue indisponible |
| ID fiable comparable et concordant | Accepter si aucune contradiction forte | preuve indépendante d'identité |
| ID fiable comparable contradictoire | Rejeter | trusted-identity-mismatch |

Autres contrôles : génération réelle des variantes Blade Runner, seconde année 2017, saisons/épisodes corrects ou contradictoires/manquants, verrous, correspondances inter-provider explicites, collisions TMDB film/série, doublons cohérents/incohérents, ordre des résultats, seuils configurés, absence de mutation des sorties et lots trop grands.

## Validation et limites

**61 tests ciblés réussis : 26 existants du générateur et 35 du classement.** Compilation incluse, aucun avertissement. Aucune suite backend complète relancée. `git diff --check` et vérification whitespace des nouveaux fichiers propres.

```sh
dotnet test src/Feedarr.Api.Tests/Feedarr.Api.Tests.csproj --no-restore --filter 'FullyQualifiedName~SearchCandidateGeneratorTests|FullyQualifiedName~SearchCandidateRankerTests'
```

Log privé ignoré : `tools/parser-benchmark/local/v23b-ranking-targeted.log`.

Avant intégration future : adapter les états persistés/révisions et les résultats réels des providers, préciser la disponibilité des métadonnées de saison/épisode au niveau œuvre, valider/calibrer les seuils sur des références d'identité indépendantes et revoir les équivalences inter-provider. Deux résultats de providers différents peuvent encore provoquer une abstention conservatrice malgré un lien connu ; aucun regroupement opportuniste d'œuvres n'est effectué ici.

Aucun parser, normaliseur existant, règle de matching actuelle, API, base ou association modifié. Aucun provider externe, accès NAS/production, reparse ou rematching. Base de référence et artefacts privés préservés. Les fichiers étrangers et tous les fichiers V2.3 locaux antérieurs sont conservés.
