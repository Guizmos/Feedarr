# V2.3-D — captures providers et comparaison reproductible

**Outillage livré localement ; aucune API de recherche contactée.** Aucun accès provider explicite n'est configuré dans le fichier local d'activation attendu. Les exemples restent désactivés. Aucune réponse réelle, identité attendue ou amélioration de matching n'a été inventée.

## Échantillon et plan livrés

Plan privé : `tools/parser-benchmark/local/v23d-captures-ready/plan.json`.

**150 releases**, dont les **120 fiches précédentes** et 30 contrôles complémentaires sélectionnés déterministement : 85 sans référence de poster, 65 avec ; 64 associations protégées servant uniquement au diagnostic, dont deux corrections manuelles explicitement réservées dans les contrôles. Les 18 validations lexicales partielles existantes sont conservées ; les 150 références d'identité restent non validées.

| Catégorie | Releases |
| --- | ---: |
| Film | 28 |
| Série | 32 |
| Animation | 31 |
| Émission | 14 |
| Spectacle | 18 |
| Jeux PC | 27 |

Le plan couvre les requêtes historiques et V2, y compris titres différents, filtres d'année, langues et filtres d'image historiques. Les années hypothétiques restent étiquetées dans les candidats ; une variante sans filtre d'année est prévue pour ne pas transformer l'hypothèse en preuve. Les requêtes identiques sont mutualisées entre releases et moteurs.

| Provider | Recherches distinctes planifiées |
| --- | ---: |
| TMDB | 814 |
| TVMaze | 47 |
| IGDB | 55 |
| RAWG | 54 |
| **Total** | **970** |

970 est un nombre de recherches, pas un nombre d'appels effectués. La pagination peut demander plusieurs appels par recherche ; les budgets par défaut sont volontairement insuffisants pour lancer toute cette collecte d'un seul coup. Ils doivent être ajustés explicitement selon les droits et quotas du compte.

## Capture et provenance

`provider_capture.py` propose `prepare`, `collect` et `compare`.

- Aucune lecture des configurations/secrets SQLite, aucun démarrage des services de production. Seuls les noms de variables d'environnement sont dans la configuration locale ; leurs valeurs ne sont chargées qu'avec exécution explicite, provider activé et conditions revues.
- Endpoints HTTPS fixes des quatre APIs, sans proxy implicite ni redirection. Aucun endpoint image, téléchargement de poster, renouvellement OAuth ou autre appel caché.
- Budget global par exécution, quota journalier partagé entre dossiers et verrou exclusif de collecte. Une tentative échouée consomme aussi son quota. Une requête par seconde au minimum, une seule à la fois.
- Un 429 arrête le provider pour l'exécution et persiste le délai Retry-After. Pas de retry automatique. La reprise explicite archive les anciennes captures incomplètes ; les captures complètes sont réutilisées.
- Une capture garde provider/type, requête exacte, année réellement envoyée au serveur, langue, filtres, pagination/offsets, dates UTC, commit et empreintes des sources/outils, digest de réponse et projection minimale. TVMaze/RAWG n'ont pas de filtre serveur d'année : yearUsed reste null.
- Seuls IDs scoped, titres/alias connus, dates et présence booléenne d'image sont projetés. Ni headers, URL avec clé, corps bruts d'erreur ou secrets ne sont enregistrés. Un écho de credential ou une projection sensible provoque un refus.
- Absence de capture, JSON invalide, erreur HTTP, données sensibles, pagination interrompue et liste réellement vide sont distincts. Une réponse absente ne devient jamais une liste vide.

Les clients actuels sont réutilisés pour leurs contrats et sélections offline ; leur transport de capture n'est pas appelé directement : certains chemins des clients écrasent les erreurs en résultats vides et dépendent du resolver/stats applicatif. Le transport local dédié conserve ces distinctions sans consulter de configuration en base.

Les plafonds officiels consultés sont de quatre requêtes/s pour [IGDB](https://api-docs.igdb.com/#rate-limits) et au moins vingt appels/10 s pour [TVMaze](https://www.tvmaze.com/api#rate-limiting). [TMDB demande de respecter ses 429 et précise que ses limites peuvent évoluer](https://developer.themoviedb.org/docs/rate-limiting). Le débit conservateur du collecteur ne remplace pas les quotas du compte ni les conditions d'utilisation. La documentation RAWG n'a pas pu être consultée pendant ce lot : son budget et ses conditions doivent être vérifiés dans le compte/documentation officielle avant activation. Aucun provider n'est activé implicitement, même TVMaze.

## Activation locale

Le scénario de langue explicite préparé est la langue TMDB par défaut puis fr-FR. Confirmer la langue pertinente pour la configuration historique autorisée ; un autre scénario doit utiliser un nouveau dossier et `--languages default,en-US`, par exemple. Aucune préférence n'a été extraite de la base.

```sh
python3 tools/parser-benchmark/provider_capture.py prepare \
  --out tools/parser-benchmark/local/v23d-captures-ready \
  --languages default,fr-FR
```

Le plan et les références existants ne sont pas écrasés lorsqu'une version change ; un nouveau dossier est requis. Les fiches annotées ne sont jamais réinitialisées.

Copier `provider-config.example.json` vers `tools/parser-benchmark/local/provider-capture.config.json`. Sur POSIX, conserver le mode 0600 ; sous Windows, restreindre les ACL au compte utilisateur. Activer uniquement les providers autorisés (`enabled=true`, `termsReviewed=true`) et fixer les budgets réellement disponibles. Définir via le gestionnaire de secrets local les variables référencées : TMDB bearer, IGDB client ID et bearer déjà obtenu, RAWG key. Aucune valeur de secret dans une commande partagée, ce rapport ou Git. TVMaze exige aussi une activation explicite.

D'abord, simulation sans réseau :

```sh
python3 tools/parser-benchmark/provider_capture.py collect \
  --plan tools/parser-benchmark/local/v23d-captures-ready/plan.json \
  --config tools/parser-benchmark/local/provider-capture.config.json \
  --out tools/parser-benchmark/local/v23d-captures-ready/captures
```

Après vérification de la configuration et du budget, ajouter `--execute` pour la collecte réelle. `--retry-incomplete` demande explicitement une nouvelle tentative des recherches incomplètes et conserve leurs anciennes versions. Un token expiré doit être renouvelé par la procédure autorisée habituelle ; l'outil ne récupère pas de client secret et n'effectue pas d'authentification automatique. Un verrou restant après interruption ne doit être retiré qu'après vérification qu'aucun collecteur n'est encore actif.

## Comparaison et vérité terrain

```sh
python3 tools/parser-benchmark/provider_capture.py compare \
  --plan tools/parser-benchmark/local/v23d-captures-ready/plan.json \
  --captures tools/parser-benchmark/local/v23d-captures-ready/captures \
  --references tools/parser-benchmark/local/v23d-captures-ready/references.json \
  --out tools/parser-benchmark/local/v23d-captures-ready/comparison
```

Le classement utilise uniquement les captures liées aux requêtes de chaque moteur. Une release n'est évaluable que si toutes ses requêtes requises sont couvertes, avec pagination complète, sources et digests conformes, dans une fenêtre de collecte de 24 h. Les captures synthétiques sont refusées comme preuves réelles. Un dépassement de capacité du ranker est une insuffisance opérationnelle, pas un rejet d'identité.

Les sélections historiques réutilisent le scorer/policy TMDB, le bloc TVMaze et les sélections IGDB/RAWG extraits des sources réelles ; leur compilation échoue si les contrats changent. Le nombre historique de candidats effectivement considérés est conservé (premiers 10 par recherche TMDB/TVMaze, premiers 5 pour IGDB/RAWG). Les scores et motifs disponibles sont conservés ; aucune confiance n'est inventée pour un client qui n'en produit pas.

**Périmètre : sélection d'identité avant téléchargement.** Cache, configuration live et succès de téléchargement ne sont pas rejoués. Le marqueur technique de présence d'image reproduit uniquement le prédicat historique ; V2 ne prend pas cette présence comme preuve d'identité. Les diagnostics sur associations protégées sont séparés du garde actif, qui interdit leur remplacement.

Chaque fiche privée contient brut, observations historique/parsing, propositions et référence d'identité vide. Pour valider une identité : renseigner `status=validated`, `outcome=identity-verified`, provider/type/ID dans `expected`, reviewer, date avec timezone et preuve de vérification indépendante. Les équivalences inter-providers exigent leurs propres IDs indépendamment vérifiés dans `equivalents`. Ne pas copier automatiquement les IDs stockés ou les propositions du moteur. Une ambiguïté légitime reste une référence ambiguë, pas une identité unique forcée.

Les références ne sont utilisées qu'après les décisions, jamais pour modifier les requêtes ou scores. Seules les fiches d'identité validées et couvertes contribuent aux métriques. Le taux d'identification inclut les abstentions dans son dénominateur ; la précision utilise les acceptations seulement. Un namespace non vérifié est compté comme non comparable, pas comme une erreur ou un succès. Les cohortes protégées et non protégées restent séparées ; ce sont des mesures diagnostiques, pas des mises à jour de production.

## Résultat actuel et validation

- **Zéro appel réel**, zéro capture réelle, zéro release avec couverture provider suffisante.
- **Zéro référence d'identité validée** ; taux d'identification et précisions restent **null**, aucun gain revendiqué.
- **46 tests ciblés réussis** : quatre projections provider, absence/erreur/vide, pagination, budgets, cooldown, cache, provenance, secrets, concurrence, reprise, échantillonnage, références et sélections offline des quatre providers.
- Les tests utilisent des transports et fixtures explicitement synthétiques, jamais comptés dans les résultats réels. Compilation Release du runner/API via les tests ; aucune suite backend complète.
- Parser, ranker, associations et corrections manuelles inchangés. Aucune écriture/migration SQLite, aucun NAS/Docker/production. Fichiers locaux étrangers préservés ; aucun commit/push.

À poursuivre après activation autorisée : collecter les réponses, effectuer la revue indépendante des 120 fiches et des contrôles, rejouer la comparaison, puis analyser les faux positifs et abstentions avant toute calibration. Aucune nouvelle heuristique n'a été ajoutée pour améliorer artificiellement les chiffres.

Fichiers principaux : `provider_capture.py`, `capture_history.py`, `IdentificationRunner/CaptureComparison.cs`, adaptateurs du runner enrichis, `tests/test_provider_capture.py`. Tous les plans, captures, références et rapports détaillés restent sous `tools/parser-benchmark/local/`, ignoré par Git. Log ciblé : `local/v23d-tests.log`.
