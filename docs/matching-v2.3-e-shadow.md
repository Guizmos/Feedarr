# V2.3-E — Matching shadow strict

Implémentation locale sur `beta`, à partir de `a78b23c`. Aucun changement du parser,
ranker, des requêtes historiques ou de la sélection persistée. Aucun appel provider
réel pendant les validations.

## Activation et périmètre

Le réglage de configuration initial a été remplacé par le contrôle volatil V2.3-F :
**OFF à chaque démarrage**, activation à chaud dans Maintenance, arrêt immédiat et
remise à zéro. Voir [V2.3-F](matching-v2.3-f-shadow-beta.md).

Le premier point d’intégration est la sélection TMDB fraîche de `FetchPosterCoreAsync`,
après le classement historique et avant ses écritures existantes. Le shadow reçoit
la liste réellement obtenue, dédupliquée par identité typée, et le choix historique
pour les IDs à cet endroit. Ce choix n’est pas une preuve de vérité terrain et ne
représente pas nécessairement le provider final du poster après les fallbacks.

Les retours de cache et le refresh manuel disposent également d’un point d’observation.
Les branches directes des autres providers et les autres méthodes de refresh ne sont
pas instrumentées dans ce premier incrément. Aucun résultat TVMaze/RAWG/IGDB n’est
converti artificiellement en résultat TMDB.

## Évaluation et couverture

`MatchingShadowEvaluator` appelle les composants existants `SearchCandidateGenerator`
et `SearchCandidateRanker`. Il expose une observation structurée : décision historique,
identité avec provider/type/ID, résultat V2, scores, raisons, contradictions,
informations manquantes et protection de l’existant.

L’observation des métadonnées TMDB conserve exactement les requêtes historiques
(langues, année et fallbacks). Une réponse n’est complète que si les métadonnées
`page`, `total_pages`, `total_results` prouvent sa couverture intégrale, sans troncature
par la limite historique. Une page manquante n’est jamais téléchargée par le shadow.
Les métadonnées absentes ou mal typées ne changent pas le comportement historique.

`not_evaluable`, sans proposition V2, est retourné notamment pour :

- cache seul, réponse absente, partielle ou couverture inconnue ;
- variante générée dont la requête n’a pas été couverte par l’historique ;
- catégorie non reconnue, type non pris en charge, ID/type provider manquant ;
- identité existante ou correction manuelle protégée ;
- état persistant changé ou lecture impossible.

Une réponse vide **complète** peut produire un rejet V2 ; elle est distincte d’une
réponse absente. Les années générées conservent leur niveau de preuve ; aucune année
hypothétique n’est promue en année confirmée. Le chemin historique fournit parfois
un ensemble plus large (recherches sans année, film et série) : le ranker reçoit cet
ensemble réel et traite les contradictions de type, sans filtrer un concurrent gênant.

## Isolation et concurrence

Le composant n’a aucune capacité d’écriture ou d’appel HTTP. Le pipeline relit l’état
persisté avec une connexion SQLite `ReadOnly` et `query_only`, sans `Db.Open()`, migration
ou changement de journal. L’état et la révision sont vérifiés avant et après un
classement publiable. Une correction concurrente invalide le résultat shadow.

Les identités établies, corrections manuelles, overrides de titre et anciens posters
manuels sont protégés. Un ID TMDB sans type reste explicitement inconnu. Le poster
n’entre pas dans l’évaluation. La sélection historique et ses protections existantes
restent seules responsables des écritures de production.

Les observations restent dans une file mémoire bornée à 64 éléments par service.
V2.3-F ajoute leur projection authentifiée, filtrée et sans cache vers l’interface
Beta. Aucun journal d’activité, fichier ou télémétrie supplémentaire. Ce lot E
n’apporte pas de mesure de précision en production.

## Validation locale

- 25 nouveaux tests shadow : exactitude/contradictions, concurrents proches,
  réponse vide/manquante/partielle/multipage, type explicite, variante non couverte,
  protections persistantes, correction concurrente, exception de lecture, désactivation,
  indépendance du poster, comparaison activé/désactivé et cache sans recherche ajoutée.
- Le lecteur est vérifié sur SQLite temporaire, y compris le flag manuel entier ;
  contenu du fichier et du WAL inchangé pendant les observations seules.
- 98 tests ciblés réussis (shadow, génération, classement et requêtes parser/provider).
- 56 tests de contrats historiques et matérialisation/identités/métadonnées réussis.
- Build API Release : 0 avertissement, 0 erreur.
- `git diff --check` propre. Aucune suite backend complète relancée.
- Copie SQLite privée : SHA-256 inchangé
  `23a3d0a355958f2eec8ed562c524282c5b5259a5ff70060991e23bedda7b159d`.

Aucun NAS, Docker, provider réel, reparse, rematching ou téléchargement en masse.
Aucun commit/push dans cette étape. Aucun gain de précision revendiqué sans références validées.
