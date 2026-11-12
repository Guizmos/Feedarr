# Matching V2 : CLI autonome

Point d’entrée unique : `python3 tools/matching-v2.py` (Python 3.9+ ; Node pour comparer). Utilise uniquement les API Shadow/Replay existantes, sans changer les décisions historiques. Le mode par défaut est `offline`, sans requête provider.

## Configuration locale

Aucun accès Beta n’était configuré dans les variables ou fichiers locaux vérifiés. Prérequis : une instance Feedarr accessible et une authentification autorisée existante. Ne pas utiliser la copie SQLite pour récupérer des credentials.

Configuration facultative, à conserver sous `tools/parser-benchmark/local/feedarr-api.json` (ignoré par Git) :

```json
{
  "baseUrl": "http://localhost:5003",
  "usernameEnv": "FEEDARR_USERNAME",
  "passwordEnv": "FEEDARR_PASSWORD",
  "database": "/chemin/instance-locale/feedarr.db"
}
```

Fournir les valeurs d’authentification via l’environnement du processus, jamais dans les arguments ou le dépôt. Alternative session existante : retirer les références Basic et utiliser `cookieEnv: "FEEDARR_SESSION_COOKIE"`, éventuellement `csrfEnv: "FEEDARR_CSRF_TOKEN"`. À défaut le jeton CSRF est obtenu par l’API de session existante et reste en mémoire. HTTPS obligatoire hors localhost ; aucun suivi de redirection ni désactivation TLS.

## Commandes

Depuis la racine du dépôt :

```sh
python3 tools/matching-v2.py probe --config tools/parser-benchmark/local/feedarr-api.json
python3 tools/matching-v2.py diagnostics --config tools/parser-benchmark/local/feedarr-api.json
python3 tools/matching-v2.py run --config tools/parser-benchmark/local/feedarr-api.json --ids 123,456 --categories films --repeat 2 --timeout 120
python3 tools/matching-v2.py run --config tools/parser-benchmark/local/feedarr-api.json --divergences --categories films,series --filter without_poster
python3 tools/matching-v2.py compare --before /chemin/rapport-avant.json --after /chemin/rapport-apres.json
```

Les IDs sont des exemples à remplacer. `--repeat` reprend exactement les IDs de la première sélection. Chaque exécution épingle les IDs du preview et refuse un Replay déjà actif. Comparaison hors ligne via le comparateur existant du frontend : décisions, couverture, identités et variations des projections providers. Les rapports restent privés sous `tools/parser-benchmark/local/api-replay/` (fichiers 0600).

`--mode tmdb --budget 10` autorise explicitement les appels du Replay TMDB existant, maximum 30. Aucun appel provider n’est fait par `probe`, `diagnostics`, `compare` ou en mode `offline`. Le CLI ne modifie pas le réglage Shadow.

## Intégrité et limites

`--database` doit désigner la base de **l’instance testée**, pas une copie indépendante. Le CLI ouvre SQLite en lecture seule, empreinte les tables métier avant/après et échoue si elles changent ; il ne restaure rien. Les tables de paramètres et d’authentification ne sont pas lues. `--poster-dir` ajoute un contrôle des fichiers posters. Arrêter les autres producteurs métier pendant le scénario pour attribuer les changements au Replay. Cette observation avant/après ne détecte pas une écriture entièrement annulée entre les deux mesures ; les tests backend restent nécessaires.

Pour une API distante sans accès local à sa base, `--api-only` est un consentement explicite à une intégrité **non vérifiée** ; aucune garantie d’absence d’écriture distante n’est annoncée. Une copie SQLite distante figée ne permet pas de prouver l’intégrité de l’instance active.

Timeout/interruption : demande d’arrêt conditionnée à l’ID et la génération de la session, sans arrêter une session de remplacement. Si la réponse de démarrage est perdue ou le serveur inaccessible, l’arrêt ne peut pas être garanti ; consulter `probe` et l’API existante avant de relancer. Aucun retry aveugle des mutations.

L’interface Shadow/Replay est masquée quand l’option avancée de maintenance existante est désactivée (valeur par défaut). Les API authentifiées restent disponibles indépendamment de l’affichage.

Tests ciblés CLI : `python3 -m unittest discover -s tools -p test_matching_v2.py -v`.
