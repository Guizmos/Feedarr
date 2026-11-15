# Matching V2-C — Anime / Jikan

## Architecture

La catégorie `Anime` utilise désormais : collecte Jikan bornée → projection des preuves
MAL → `SearchCandidateRanker` commun → préparation des métadonnées et de l’image →
transaction `PublishAcceptedVideo` existante. Les identités MAL restent dans l’espace
`jikan:Anime:<id>`. Aucun moteur de scoring parallèle, réglage Maintenance ou migration
SQLite n’est ajouté. Les autres catégories ne sont pas reroutées vers Jikan.

Les preuves conservent titres japonais, romanisés, anglais et alternatifs, année issue
de `year`/`aired.from`, format, nombre d’épisodes, statut et relations typées. Les titres
structurés `titles` sont prioritaires ; les anciens champs Jikan sont des fallbacks.
Les différences TV/Movie/OVA/ONA/Special sont des contradictions fortes quand le format
attendu est explicite. Les formats hors contrat, dont Music, ne deviennent pas des TV.
Les relations servent à garder une alternative non vérifiée en abstention ; aucun
nouveau graphe de franchise n’est créé en SQLite.

Les seuils globaux restent inchangés. Une relation vide ne prouve pas S01. Un ordinal
est utilisé seulement lorsqu’il est explicitement déclaré dans un titre fournisseur.
Un épisode absolu exige une réponse d’épisode liée au MAL demandé et une diffusion
connue ; le nombre total d’épisodes ne suffit pas. Les candidats concurrents restent
soumis au même écart minimal du ranker. Une année hypothétique n’est pas une confirmation.

Limites : deux pages de 25 résultats par requête, 50 candidats uniques, cinq candidats
plausibles à enrichir, douze appels logiques recherche/preuves, échéance de 60 s incluant
la préparation/publication et limitée aussi par le token du worker. Appels espacés de
1,1 s dans le processus ; pas de retries HTTP imbriqués pour les preuves. Les limites
déterministes produisent une abstention 422 ; erreurs réseau/temporaires : 503.
Le replay réutilise le même adaptateur, la limitation et le ranker, sans écritures métier.

Une protection spécifique à MAL vérifie dans la transaction que les releases partageant
l’entité ont des requêtes équivalentes, même saison et même contexte de format. Une
saison différente, un épisode hors périmètre, un autre média ou un titre manuel sur
une release sœur empêche la publication. Les doublons compatibles de même saison
réutilisent l’identité. Les groupes de plus de 100 releases restent en abstention,
sans réaffectation automatique d’entités ni modification du comportement TMDB TV.

Le poster décodable est écrit sous un nom unique avant la transaction. Identité et
métadonnées sont appliquées ensemble. Révision, identité et choix manuels sont validés ;
les identités existantes sont conservées. L’échec transactionnel laisse les anciennes
données intactes et retire le fichier non référencé. Store et audit restent dérivés.
Le titre anglais fourni est utilisé si disponible, sinon le titre fournisseur ; aucune
traduction ou date de première diffusion n’est inventée.

## Suppressions

- `ComputeMatchScore`, tri par score/popularité et choix du premier gagnant Jikan V1.
- `SearchAnimeAsync` et ses DTO exclusivement nécessaires à cette sélection.
- Publication Anime V1 en écritures séparées poster/métadonnées.
- Confiance fixe 0,70 et alimentation du cache historique pour décider un nouveau match.

Les téléchargements d’images, statistiques, transport public/SSRF et services utiles aux
autres catégories restent réutilisés. Aucun changement du parser général.

## Validation exécutée et limites réelles

Le snapshot de 305 releases et l’instance locale configurée ne contiennent **aucune
release classée Anime**. Le contrôle hors réseau des 305 IDs, avec les captures figées,
produit **zéro changement de décision ou d’identité proposée, zéro appel réseau**.

Un complément persistant hors Git contient 11 vraies releases / 9 œuvres issues de ce
snapshot : Redline, Evangelion 3.0, Le Magicien d’Oz, The Seven Knights of the Marronnier
Kingdom, Run with the Wind, Horror Collector, Digimon Adventure, Even the Student Council
Has Its Holes et Liz et l’Oiseau Bleu. La copie figée garde ses catégories d’origine.
Une copie dérivée applique explicitement le contexte Anime aux seuls IDs sélectionnés.
Cela ne constitue pas une migration de ces releases en production, ni un gain mesuré.

**Jikan est injoignable depuis cet environnement.** La collecte a été arrêtée après
huit tentatives : sept timeouts enregistrés et une interruption. Aucune réponse n’a été
capturée. Un contrôle de connectivité direct a également échoué (HTTP 000, délai de
connexion de 5 s). Le endpoint configuré est bien le fournisseur officiel.

Le vrai pipeline a été exécuté sur la release non associée 52, avant/après, sur deux
copies SQLite isolées, sans démarrage de services hébergés :

| Mesure | Avant V1 | Après V2 |
| --- | --- | --- |
| Tentatives réseau | 1 | 1 |
| Durée | 20,085 s | 20,141 s |
| Résultat | TaskCanceledException | 503 explicite |
| Identité / poster publiés | aucun | aucun |

**Publications réelles réussies : 0.** Comparaison des acceptations, abstentions
sémantiques, faux positifs et gains Anime : **non évaluable sans réponses fournisseur**.
Les labels du complément restent non vérifiés. Aucun gain n’est fabriqué à partir des
anciennes associations TMDB. Les cas OVA/ONA/Special ne sont pas représentés par des
releases locales vérifiées ; leurs contradictions sont couvertes uniquement par tests.

285 tests ciblés ont réussi en Release, dont les scénarios de partage d’entité MAL.
Les tests Jikan ont ensuite été relancés avec des doublons présentant une casse différente. Ils couvrent formats, titres modernes et
multilingues, vrais concurrents étayés, ordinal contradictoire malgré un alias exact,
épisodes absolus, preuves absentes, budgets, annulation, révision, publication, rollback,
idempotence et identités manuelles. Compilation Release et `git diff --check` passent.
Les succès de publication dans ces tests utilisent des réponses HTTP simulées et des
images décodables : ils ne valident pas un téléchargement réel depuis Jikan.
Aucune suite complète ni validation navigateur n’a été lancée.

## Reprise de la validation réelle

Dossiers privés hors Git : `~/Documents/Projets/Feedarr-benchmarks/anime-c/` et
`~/Documents/Projets/Feedarr-benchmarks/anime-c-diagnostic/`.
Le premier conserve la copie d’origine, le binaire avant migration et les tentatives
réelles ; le second le contexte diagnostique, les captures et labels non vérifiés.
La référence originale `reference-locale` n’est pas modifiée par ces copies.

Lorsque Jikan redevient accessible depuis le Mac :

```sh
./tools/matching-benchmark/bench.py replay anime-c-diagnostic reprise-jikan --record --max-requests 60
./tools/matching-benchmark/bench.py replay anime-c-diagnostic verification-jikan --same-as reprise-jikan
```

Le run `collecte` interrompu reste incomplet et ne doit pas servir de comparaison valide.
Il reste à vérifier les identités MAL distinctes, rejouer V1 sur les mêmes réponses,
compléter les labels et réussir des publications réelles. Les ordinaux de saisons MAL
non prouvés restent volontairement en abstention ; cette couverture doit être mesurée.

Source de contrat : [OpenAPI officiel Jikan v4](https://raw.githubusercontent.com/jikan-me/jikan-rest/master/storage/api-docs/api-docs.json).
