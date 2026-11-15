# Matching V2 — séries et émissions

Les catégories `Serie` et `Emission` passent par le parcours vidéo V2 existant :
pages TMDB complètes et recherches TVMaze → preuves TV → ranker commun → préparation
réseau/image → transaction SQLite commune. Films, animations et spectacles confirmés
comme films conservent leur parcours. Aucun réglage Maintenance ni migration SQLite ajouté.

Les titres français, originaux et alternatifs sont rattachés à une identité typée.
Le regroupement entre fournisseurs exige des identifiants externes vérifiés sans
contradiction. Les saisons/épisodes et dates apportent une preuve indépendante ; une
information manquante ou un 404 ne prouvent rien. La date d'une émission n'est pas
l'année de création de sa série. Les seuils et le traitement des concurrents restent
communs. Une année laissée dans un titre de saison peut produire une seconde hypothèse,
sans modifier le parser général et sans filtrer les concurrents par année côté HTTP.

Limites : six candidats plausibles à enrichir, douze recherches TMDB, trente appels
recherche/preuves, deadline de 60 s partagée avec la publication (et deadline externe
du worker inchangée). Budget ou couverture insuffisants : abstention. TVMaze peut
publier une identité propre ; Fanart peut enrichir uniquement la série TMDB acceptée,
après vérification de son identifiant TVDB.

La transaction et les contrôles de révision existants protègent publication,
associations manuelles et identités déjà établies. Les fichiers sont écrits avant
commit avec un nom unique ; un échec ne laisse aucune association partielle.
Les optimisations de stockage restent dérivées. Les métadonnées existantes ne sont
pas rematchées pour changer de fournisseur.

## Suppressions

- Décision automatique TVMaze V1, ses seuils, scorer et ordre de priorité.
- Entrées séries/émissions du dispatcher historique ; les autres catégories gardent
  les services dont elles ont besoin.
- Quatre snapshots golden de priorité/fallback V1, remplacés par quatre contrats
  d'intégration V2 : publication TVMaze, TMDB TV, Fanart vérifié, abstention émission.
- Wrapper de publication réservé aux films, remplacé par la transaction vidéo commune.

## Validation locale

Référence privée hors Git : `~/Documents/Projets/Feedarr-benchmarks/reference-locale/`.
305 IDs figés, dont 119 séries et 13 émissions. Les deux versions sont rejouées hors
réseau sur le même snapshot et le même stock de réponses publiques. Aucun fetch RSS.
Le rejeu ne publie aucune association. Il mesure les décisions proposées, pas le taux
historique de réussite du V1 ni la justesse de toutes les associations existantes.

Résultat du rejeu final sur les 132 releases TV :

| Décision proposée | Avant | Après |
| --- | ---: | ---: |
| Acceptation | 4 | 105 |
| Ambiguïté | 86 | 8 |
| Rejet | 38 | 5 |
| Non évaluable | 4 | 14 |

Sur les 28 releases sans identité, les propositions acceptées passent de 1 à 17
(+16 propositions, à distinguer des gains vérifiés). Dix labels ont été revus ; neuf
sont comparables avec la référence : **9 gains corrects, 0 régression et 0 faux positif
observé sur ce sous-ensemble**. Aucune conclusion de justesse exhaustive n'est tirée
pour les cas non revus. Les décisions des 173 autres releases restent identiques.

Les 14 cas TV non évaluables correspondent à : pagination dupliquée (1), captures
réseau manquantes (4), preuves non capturées par les gardes du benchmark (4), budget
de candidats (3), contradictions entre dates des fournisseurs (2). Ces deux derniers
cas s'abstiennent plutôt que choisir silencieusement une date. Les accès HTTP logiques
du replay passent de 261 à 807 ; les rejeux comparés font **zéro appel réseau réel**.

La revue progressive des labels utilise les noms officiels, dates, saisons et métadonnées
fournisseur ; elle ne transforme pas les associations historiques en vérité. Les cas
non revus et les réponses manquantes restent explicitement non évaluables.

Sur neuf IDs identiques, le vrai parcours de publication a aussi été exécuté avant/après
sur deux copies SQLite fraîches, avec les clients protégés réels et sans services hébergés :

| Mesure | Avant | Après |
| --- | ---: | ---: |
| Retours réussis, y compris identité préservée / entité partagée | 2 | 8 |
| Appels HTTP observés | 42 | 62 |
| Durée totale séquentielle, temporisation de 1 s incluse | 48,2 s | 67,1 s |

La Diplomate, Mauvais Karma, The Queen Who Crowns, Inspecteur Barnaby et Star Academy
publient la bonne identité et un poster décodable. Les releases partageant La Diplomate
réutilisent ensuite l'entité publiée sans appel fournisseur. Dark conserve son identité
et ses métadonnées sans nouvel appel. C politique s'abstient faute de preuve de diffusion.
Pour The Queen Who Crowns, TMDB ne fournit pas de résumé français : aucun résumé n'est inventé.

FBI est absent des 305 : sa vraie release du précédent diagnostic est rejouée séparément,
sans l'ajouter à la cohorte. Avant : ambiguïté ; après : TMDB TV 80748, preuves S08E05.

291 tests ciblés passent : preuves, concurrents, dates, identités manuelles/existantes,
publication/rollback, révisions/concurrence, annulation et délais, recherche multilingue,
quotas et lecture seule du replay. Compilation Release et `git diff --check` passent.
Aucune suite backend complète, synchronisation RSS ou validation navigateur exécutée.
