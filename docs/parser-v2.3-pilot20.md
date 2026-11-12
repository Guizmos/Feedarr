# V2.3 — premier lot de captures, 20 releases

Plan privé préparé : `tools/parser-benchmark/local/v23-pilot20/plan.json`.
Il est dérivé du plan V2.3-D validé, avec priorité aux différences historique/V2 :
20 releases, 18 différences de requête, 11 sans poster et 9 avec poster ; les six
catégories sont couvertes. Neuf associations protégées, dont deux corrections
manuelles, sont utilisées uniquement pour diagnostic. Aucune capture réelle lancée.

## Configuration requise

Exemple sans secrets versionné :
[`provider-capture.example.json`](../tools/parser-benchmark/provider-capture.example.json).
Copie locale déjà prête et désactivée :
`tools/parser-benchmark/local/v23-pilot20/provider-config.json`.

Le format est celui réellement validé par `load_config` : `version=1`,
`maxRequestsTotal`, `maxPages`, puis les quatre providers avec `enabled`,
`termsReviewed`, `maxRequestsPerDay`, `minIntervalSeconds` et `credentialEnv`.
Les valeurs de `credentialEnv` sont **des noms de variables**, jamais des secrets :

| Provider | Références d'environnement |
| --- | --- |
| TMDB | bearer → FEEDARR_CAPTURE_TMDB_BEARER |
| TVMaze | aucune, mais activation explicite nécessaire |
| IGDB | clientId → FEEDARR_CAPTURE_IGDB_CLIENT_ID ; bearer → FEEDARR_CAPTURE_IGDB_BEARER |
| RAWG | apiKey → FEEDARR_CAPTURE_RAWG_KEY |

Après vérification des autorisations et quotas du compte, modifier seulement la
copie locale : activer les providers autorisés et `termsReviewed`. Fournir leurs
variables via le gestionnaire de secrets local, sans les afficher. Le bearer IGDB
doit déjà être obtenu : aucun client secret ou renouvellement OAuth automatique.
Sur POSIX, fichier en 0600 ; sous Windows, ACL limitées au compte utilisateur.
Aucun credential n'est lu dans SQLite.

Le scénario de langue est TMDB par défaut puis fr-FR. Les empreintes du code,
requêtes, années et filtres sont figées dans le plan ; un code incompatible ou une
capture partielle interdit une mesure de précision.

## Budget du pilote

Configuration préparée : deux pages maximum, au moins une seconde entre les
appels, plafond global 288 tentatives et plafonds journaliers suivants :

| Provider | Recherches distinctes | Pages maximum | Appels maximum configurés |
| --- | ---: | ---: | ---: |
| TMDB | 124 | 2 | 248 |
| TVMaze | 8 | 1 (sans pagination) | 8 |
| IGDB | 8 | 2 | 16 |
| RAWG | 8 | 2 | 16 |
| **Total** | **148** | | **288** |

Les appels déjà consommés dans le quota partagé UTC réduisent ces plafonds ; les
captures réutilisées ne consomment pas de nouvel appel. Un 429 arrête le provider
et persiste Retry-After. Aucun retry automatique. Une pagination au-delà de deux
pages reste incomplète : ne pas l'interpréter comme une absence de résultat ni
élargir les limites sans examen du lot.

## Commandes depuis la racine Feedarr

La configuration livrée reste désactivée. Vérifier d'abord sans réseau :

```sh
python3 tools/parser-benchmark/provider_capture.py collect \
  --plan tools/parser-benchmark/local/v23-pilot20/plan.json \
  --config tools/parser-benchmark/local/v23-pilot20/provider-config.json \
  --out tools/parser-benchmark/local/v23-pilot20/captures
```

Après activation locale explicite et mise à disposition des variables autorisées,
la première collecte réelle se lance avec :

```sh
python3 tools/parser-benchmark/provider_capture.py collect \
  --plan tools/parser-benchmark/local/v23-pilot20/plan.json \
  --config tools/parser-benchmark/local/v23-pilot20/provider-config.json \
  --out tools/parser-benchmark/local/v23-pilot20/captures \
  --execute
```

**Cette commande avec `--execute` n'a pas été exécutée pendant la livraison.**
Aucun téléchargement d'image, accès NAS ou écriture d'association n'est effectué
par ce flux. Plan, configuration, captures et références restent ignorés par Git.

## Examiner avant élargissement

1. Lire `captures/collection-summary.json` et les états des captures : budgets,
   erreurs, 429, pagination et provenance. Ne pas remplacer une erreur par `[]`.
2. Rejouer la comparaison offline :

```sh
python3 tools/parser-benchmark/provider_capture.py compare \
  --plan tools/parser-benchmark/local/v23-pilot20/plan.json \
  --captures tools/parser-benchmark/local/v23-pilot20/captures \
  --references tools/parser-benchmark/local/v23-pilot20/references.json \
  --out tools/parser-benchmark/local/v23-pilot20/comparison
```

3. Dans `comparison/comparison.json`, vérifier les lacunes de couverture, décisions,
   identités, scores, contradictions et gardes des cas protégés. Une couverture
   insuffisante ou une fenêtre supérieure à 24 h reste non évaluable.
4. Remplir les références d'identité après vérification indépendante : provider,
   type, ID, reviewer, date et preuve. Les IDs persistés et propositions restent
   des observations. Relancer la comparaison pour les seules références validées.
5. Examiner faux positifs, abstentions et cohortes protégées séparément avant de
   choisir un prochain lot. Aucun gain n'est déduit du nombre de posters ni des
   références non validées. Aucune activation automatique de V2 en production.

Les procédures détaillées restent dans le
[rapport V2.3-D](parser-v2.3-d-provider-captures.md).
