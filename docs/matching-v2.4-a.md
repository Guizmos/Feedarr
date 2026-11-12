# V2.4-A — Matching actif des nouveaux films

La publication et la persistance de cohorte sont renforcées par [V2.4-B](matching-v2.4-b.md), qui remplace les limites décrites ci-dessous pour le chemin actif.

## Routage et décision

`PosterFetchService.FetchPosterCoreAsync` route le périmètre activé **avant** le cache de correspondances et la réutilisation d’une autre release. Le cache fichier existant est également écarté pour ce périmètre. `PosterMatchingOrchestrator.FetchPosterAsync` appelle `FetchMovieV2Async` : seul `SearchCandidateRanker.Rank(...).Decision == Accept` permet d’entrer dans `CompleteTmdbMatchAsync`.

Le générateur, le parser et les seuils du ranker restent inchangés. Les autres catégories et V2 OFF utilisent le chemin historique. Les identités établies du périmètre sont préservées sans nouvelle recherche ; les corrections d’identité manuelles conservent leur chemin dédié existant. Une correction de titre manuel ou une modification concurrente fait s’abstenir V2.

## Configuration

Configuration `App` existante, sans écran supplémentaire :

```json
{
  "App": {
    "MatchingV2MoviesEnabled": false,
    "MatchingV2MoviesMinReleaseId": 123456,
    "MatchingV2MoviesSearchBudget": 12,
    "MatchingV2MoviesMaxPagesPerQuery": 3
  }
}
```

`123456` est un exemple : fixer le seuil au **prochain ID de release**, déterminé en lecture seule avant le pilote, pour exclure le stock existant. L’activation exige un seuil positif explicite inférieur à `long.MaxValue` ; sinon la validation de configuration empêche le démarrage. Par défaut le booléen est faux et le seuil vaut `long.MaxValue`.

Variables équivalentes : `App__MatchingV2MoviesEnabled`, `App__MatchingV2MoviesMinReleaseId`, `App__MatchingV2MoviesSearchBudget`, `App__MatchingV2MoviesMaxPagesPerQuery`. Les options sont lues à la construction du service : appliquer un redémarrage contrôlé pour activer/désactiver, sans modification des protections réseau.

## Couverture et budgets

Plan borné : au maximum deux titres générés, chacun recherché dans la langue de métadonnées configurée et en anglais (langues identiques dédupliquées). Aucune année ne filtre la recherche : une hypothèse ne devient pas une année confirmée et les remakes restent visibles au ranker.

Chaque recherche doit couvrir toutes ses pages, au maximum trois pages chacune et douze requêtes de recherche au total, sans retry HTTP automatique sur ces recherches. Métadonnées de pagination manquantes, incohérentes, résultats dupliqués entre pages, limite dépassée, erreur ou timeout conduisent à une abstention, même après un excellent résultat. La limite de résultats du ranker reste inchangée (100 projections d’identité/année). Le traitement recherche/publication est borné par un timeout de 60 secondes et l’annulation du caller.

Le budget de douze concerne les recherches d’identification. Après Accept, les appels habituels d’images, détails, crédits et éventuellement Fanart pour **la même identité** restent ceux de la publication existante, avec ses limites/retries habituels. Aucun autre candidat n’est sélectionné pour trouver un poster. Les réponses Search ne constituent pas une couverture de tous les titres alternatifs officiels : faute de preuve suffisante, V2 s’abstient.

## Écritures, concurrence et rollback

La publication TMDB/Fanart est extraite sans changer ses writers : `BeginMatching`, `ValidateMatching`, `SavePoster`, stockage et enrichissement existants. Relecture de l’état après ranking, puis contrôles de révision lors des écritures ; contrôle supplémentaire avant l’écriture du fichier téléchargé. Identité, détails et poster sont demandés pour le même ID TMDB typé `movie`. Les décisions non Accept ne téléchargent pas de poster et n’empruntent aucun fallback décisionnel historique. Une erreur de publication après une écriture déjà validée ne déclenche aucun choix alternatif ; le chemin existant n’est pas une transaction globale entre réseau, SQLite et fichiers.

Rollback : remettre `MatchingV2MoviesEnabled=false` et reconstruire le service par redémarrage. Les prochains traitements reprennent l’historique ; les associations acceptées restent présentes, sans purge ni rematching de rollback. Aucun travail global n’est lancé à l’activation ou à la désactivation.

## Validation Beta minimale

1. Sur une instance de test autorisée, relever le prochain ID et renseigner le seuil ; activer le booléen puis redémarrer de façon contrôlée.
2. Laisser arriver quelques nouveaux films par le flux normal, sans reparse global. Vérifier les logs existants `Matching V2 movie accepted/abstained` et les associations/posters obtenus.
3. Vérifier un cas accepté, un cas ambigu/rejeté et une identité protégée. Une abstention peut laisser une release sans poster ; c’est attendu.
4. Désactiver et redémarrer ; vérifier le retour au chemin historique sur un nouveau film. Ne pas utiliser Replay comme preuve d’une écriture active : il reste isolé et diagnostique.

Tests : `ActiveMovieMatchingTests` couvre OFF/ON, cache historique, abstentions, pagination/compétiteurs, variantes hypothétiques, budgets, erreurs/timeouts, type contradictoire, identité protégée, concurrence, entity partagée et absence de sélection alternative pour un poster. Les contrats historiques et tests de résilience existants complètent ces contrôles. Tous utilisent des dossiers/SQLite temporaires et des transports simulés, sans provider réel.
