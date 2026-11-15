# Benchmark local du replay Matching V2

Depuis la racine de `beta`, Python 3.9+ et .NET 8, sans nouveau package :

```sh
./tools/matching-benchmark/bench.py capture reference-locale
./tools/matching-benchmark/bench.py replay reference-locale avant --record --limit 20 --max-requests 60
# Après une modification du moteur : les mêmes IDs et réponses, sans réseau ni clés.
./tools/matching-benchmark/bench.py replay reference-locale apres --same-as avant
./tools/matching-benchmark/bench.py compare reference-locale avant apres
```

Chaque nom est nouveau ; rien n’est écrasé. La référence de validation `reference-locale`
existe déjà sur ce Mac, avec les runs `baseline` et `candidate`. Utiliser un autre nom
pour une nouvelle capture. Sans `--ids`/`--limit`, le rejeu vise tous les IDs figés.
Le budget de 60 requêtes est global : les cas non couverts restent non évaluables.
On peut choisir explicitement `--ids 5,21,152,268,271,531,814` et augmenter
`--max-requests` jusqu’à 1000 pour une collecte plus large, sans synchroniser le RSS.

## Données et confidentialité

Stockage par défaut : `~/Documents/Projets/Feedarr-benchmarks/REFERENCE/`.
`--root` accepte un autre dossier persistant sous le compte utilisateur, hors dépôt.

- `snapshot.db` : **backup SQLite complet privé**, cohérent, non modifié par les rejeux.
  Il peut contenir configuration/URLs privées et credentials chiffrés : ne pas le partager.
- `manifest.json` : IDs, titres et parsing stockés, catégories et identités existantes.
  Les textes manifestement sensibles sont masqués dans cet export, pas altérés dans la base.
- `captures/` : réponses réduites à pagination, IDs, titres officiels, années et langues, ainsi que preuves TV (saisons, épisodes, dates et identifiants externes).
  Aucune clé, en-tête d’authentification, URL de requête, image ou payload brut conservé. Seuls les liens publics TVMaze de rattachement épisode/série sont conservés. Les titres d’épisodes, inutilisés pour décider, sont exclus.
  Les fichiers sont nommés par empreinte du plan public, excluant `api_key`.
- `runs/` : décisions, candidats, scores, contradictions, identité proposée, couverture,
  empreintes des preuves/plans, durées, requêtes du replay et **vrais appels réseau séparés**.
- `labels.json` : références de justesse à renseigner après revue ; jamais déduites des IDs stockés.
- `comparison-*.json` : différences de décision et catégories de qualité.

Dossiers privés 0700, fichiers 0600. Les clés de l’instance ne sont ni copiées ni affichées.
`--record` lit le keyring local (`--keys`), sans créer/changer ses clés. L’offline utilise
un credential factice jamais transmis et fonctionne sans keyring. Le snapshot est contrôlé
par SHA-256 avant/après ; aucune migration, écriture d’association ou tâche hébergée démarrée.
Le backup de la base active utilise `sqlite3.Connection.backup`, `mode=ro/query_only`,
sans checkpoint et sans copier ses WAL à la main.

Limites par référence : 2000 releases, base 256 Mio, captures 32 Mio/1000 fichiers,
une réponse <=1 Mio avant projection, 20 runs de 64 Mio maximum chacun.
Les opérations concurrentes de cet outil sont refusées. Les captures existantes sont
réutilisées et jamais remplacées par une nouvelle réponse en ligne. Une capture manquante
en mode offline produit `non_evaluable`, **sans fallback réseau**.

Supprimer le dossier d’une référence pour supprimer ensemble sa base/captures/résultats.
Le dossier `.build` à la racine est régénérable et contient les seuls artefacts de compilation.

## Comparaison objective

Une identité stockée ou une acceptation V2 n’est pas une vérité. Dans `labels.json` :

```json
{"268": {"verified": true, "correctIdentities": ["tmdb:movie:519182"], "abstentionJustified": false}}
```

L’exemple explique le format ; ne marquer `verified` qu’après contrôle du vrai contenu.
Une liste vide vérifiée signifie qu’aucune association ne doit être proposée dans ce référentiel.
`abstentionJustified` permet de valider explicitement une abstention malgré une identité connue.

Le comparateur distingue nouvelle association correcte, régression, faux positif,
abstention justifiée, non évaluable ; il conserve aussi `correcte_stable`, `a_revoir` et
`abstention_a_revoir` pour ne pas inventer des verdicts. Un faux positif introduit depuis une
bonne identification est une régression avec `falsePositive=true`.

Les mêmes IDs et snapshot sont obligatoires. Avec le même jeu de captures (empreinte
globale identique), une version peut changer ses hypothèses : si toutes les réponses
nécessaires existent, le comparateur signale `evidenceChanged` et permet la revue qualité.
Sans ce jeu figé commun, des requêtes/langues/preuves différentes restent non évaluables.
Une réponse absente exige une collecte explicite puis un nouveau rejeu des deux versions
sur le même jeu complété ; une collecte live à une autre date ne garantit pas le même
état fournisseur. Ne pas assimiler une baisse de durée offline à un gain réseau.

## Périmètre réel

Le runner appelle **MatchingReplayService existant**, son générateur/ranker et son budget
sans les modifier. Il évalue aussi les releases déjà associées, sans les republier.
Il couvre la recherche TMDB movie/tv pour les cinq catégories vidéo du replay. Jeux et
médias non couverts sont explicitement non évaluables. Ce n’est pas un test du dispatch,
des preuves de type, de Fanart ou de la publication du worker MovieV2 : notamment, le replay
existant peut filtrer l’année HTTP, contrairement à la collecte MovieV2 active.

La collecte réutilise SSRF/DNS/socket validé et l’allowlist du replay, sans retries ajoutés,
espacée d’au moins une seconde. Auth refusée ou quota : arrêt des nouveaux accès réseau.
Un titre fournisseur suspect rend la réponse non évaluable plutôt que de conserver des
secrets ou masquer un concurrent pour améliorer artificiellement un score.

Validation légère de l’outil :

```sh
PYTHONDONTWRITEBYTECODE=1 python3 -m unittest discover -s tools/matching-benchmark -p test_compare.py -v
```

### Séries et émissions

Le replay et la publication partagent le même enrichissement et ranker TV. Les candidats
TMDB TV et TVMaze restent concurrents tant qu’un identifiant externe vérifié ne les
relie pas. Les preuves sont bornées à six candidats plausibles et au budget HTTP du
replay (30 appels par release dans le runner). Une saison absente, un épisode non
référencé ou une réponse non capturée restent inconnus, jamais des preuves positives.
Une date de diffusion ne remplace pas l’année de création de la série.

Le fichier privé `labels.json` contient une revue progressive, pas une vérité déduite
des identités historiques. Distinguer les nouvelles propositions des associations
réellement publiées : le replay ne modifie aucune association.
