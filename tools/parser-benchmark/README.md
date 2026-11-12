# Parser V2.1 — corpus, références et benchmark hors ligne

Diagnostic uniquement : **aucune règle du parser ou du matching n’est modifiée**.
Le runner compile les sources réelles du parser sans référencer/démarrer l’API, sans
SQLite ni client HTTP dans l’exécutable de mesure. Python utilise uniquement sa
bibliothèque standard. Prérequis : Python 3.8+ et SDK .NET 8 installé.

## Démarrage local avec les 174 cas existants

Depuis la racine Feedarr :

```sh
python3 -m unittest discover -s tools/parser-benchmark/tests -v
python3 tools/parser-benchmark/benchmark.py fixtures --out tools/parser-benchmark/local/fixtures
python3 tools/parser-benchmark/benchmark.py run \
  --corpus tools/parser-benchmark/local/fixtures/corpus.json \
  --references tools/parser-benchmark/local/fixtures/references.json \
  --out tools/parser-benchmark/local/current
python3 tools/parser-benchmark/benchmark.py check --result tools/parser-benchmark/local/current/results.json
```

Le corpus permanent `src/Feedarr.Api.Tests/Golden/parser-lot5-p0.json` est **lu**, pas
copié ni modifié dans Git. Ses attentes déjà revues au P0 deviennent des références
de non-régression du parsing. Elles ne valident aucune identité provider réelle.

Les sorties locales sont ignorées par Git. Dans le dépôt, les outils refusent un
autre emplacement que `tools/parser-benchmark/local/`. Un dossier externe local
comme `/tmp/benchmark-feedarr/` est aussi possible ; `/volumeN/` est interdit en
sortie, y compris après résolution d’un lien symbolique. Les fichiers créés sont
en mode 0600 et les nouveaux dossiers en 0700. Ne jamais committer d’export NAS,
même après filtrage heuristique.

## Export limité depuis une instance réelle

Pour une **sauvegarde cohérente autonome copiée localement**, un mode distinct permet
de ne changer aucune permission : `--local-snapshot --db tools/parser-benchmark/local/backup.db`.
Il est limité au dossier local ignoré, refuse tout WAL/SHM/journal présent et ouvre
`mode=ro&immutable=1`. Ce mode suppose une copie indépendante sans écrivain ; il ne
doit jamais être utilisé pour une base active ou un chemin NAS. Le mode NAS par
défaut et ses refus de permissions restent inchangés.

L’exporteur est autonome et n’écrit que sur stdout. Il utilise SQLite via son API,
`mode=ro`, `query_only=ON`, tris temporaires en mémoire et un authorizer refusant
écritures, lecture des colonnes de credentials, ATTACH, tables temporaires et PRAGMA
non nécessaires. Il ne copie pas la base, ne lit pas ses
fichiers à la main, ne déclenche ni checkpoint, ni synchronisation, ni requête API.

**Il exige un compte ou une vue système déjà en lecture seule** sur le répertoire,
le fichier SQLite et les sidecars présents. `mode=ro` seul n’est pas une garantie
contre une création/écriture SHM. L’outil refuse un chemin accessible en écriture
à l’utilisateur courant. Exécuter le CLI dans un processus neuf, sans connexion
SQLite d’écriture préexistante dans ce processus. Ne pas changer les permissions
de production pour ce lot. Ne pas utiliser `immutable=1` sur une base WAL active et
ne pas déplacer, copier, supprimer ou modifier les WAL/SHM.

Avec un accès SSH **déjà configuré** pour ce compte en lecture seule, exemple à
adapter au nom réel du fichier SQLite, sans installer de script sur le NAS :

```sh
umask 077
mkdir -p tools/parser-benchmark/local
ssh -T -o BatchMode=yes -o StrictHostKeyChecking=yes -o UpdateHostKeys=no \
  -o ConnectTimeout=5 ALIAS_NAS_LECTURE_SEULE \
  'python3 -B - --db /volume1/Docker/Feedarr/data/feedarr.db --size 1500 --seed feedarr-parser-v2.1' \
  < tools/parser-benchmark/export_sqlite.py \
  > tools/parser-benchmark/local/export-assaini.json
```

Vérifier le code de retour SSH avant import. En cas de refus, conserver les permissions
existantes et ne pas contourner le refus avec sudo, un conteneur, une copie complète
ou des manipulations des sidecars. Il reste possible de tester tout le benchmark
avec les fixtures. SQLite 3.25+ est nécessaire pour l’échantillonnage par fenêtres.

### Champs, sélection et confidentialité

La projection SQL est limitée aux titres/parsing stockés, catégorie, champs numériques
de provider utiles, indicateurs de poster/tentative et données de stratification.
Elle n’interroge ni les credentials, ni les paramètres des providers, ni les URLs de
sources/releases, ni GUID, cookies, raw_json ou métadonnées JSON. Les erreurs de
posters sont classées **en SQL** sans exporter leur texte brut. Les IDs de provider
non numériques sont omis ; aucun ID public opaque n’est converti arbitrairement en
un autre identifiant. Cette omission limite les analyses sur certains providers.

Les noms de sources ne sortent pas : le SQL exclut les titres/champs nettoyés/groupes
contenant un nom de source connu. Les IDs locaux/source deviennent des pseudonymes
SHA-256 à graine fixe. Ce n’est pas une anonymisation des titres eux-mêmes.
Le filtre exclut notamment URLs, hôtes courants, emails, IPv4, chemins privés,
tokens/credentials explicites et longues chaînes hexadécimales. Les schémas d’import
sont sur liste blanche. Les exports/résultats restent privés : une revue humaine
est indispensable avant tout partage, car un nom privé libre ou un identifiant
inconnu ne peut pas être détecté exhaustivement par une expression régulière.

L’échantillon vise 70 % de releases sans référence de poster et 30 % de contrôle,
puis complète avec l’autre groupe si nécessaire. Sélection déterministe : catégories,
sources pseudonymisées, ancienneté et complexité sont stratifiées ; tri pseudo-aléatoire
fixe sur ID et graine, puis sélection équilibrée par strate. La complexité est un
proxy (longueur/crochets/parenthèses), pas une vérité métier. L’ancienneté est relative
au maximum de publication de la base moins 180 jours, pas à l’horloge d’exécution.

La projection candidate est plafonnée à `max(100, 12 × taille demandée)` ; des titres
exclus peuvent réduire le résultat. Taille demandée maximale : 2000. Les lectures et
tris SQL peuvent parcourir la table et consommer de la mémoire : utiliser une période calme. Rejouer sur une
base qui évolue n’assure pas les mêmes lignes ; conserver l’export assaini local pour
les comparaisons exactes, liées au SHA-256 du corpus.

### Sens de « sans poster » et du matching

`hasPoster` reprend l’état SQL de ReleaseRepository :
`COALESCE(releases.poster_file, media_entities.poster_file)` non NULL/non vide.
Une chaîne vide côté release masque donc un poster d’entity ; ce comportement est
conservé pour ne pas changer la définition pendant la mesure. La vue bibliothèque
« Sans poster » filtre `!it.posterUrl`, construit à partir de l’état de poster exposé.
L’export mesure une **référence stockée**, pas l’existence ou la lisibilité physique
du fichier ; il ne parcourt pas les dossiers posters.

`matchStatus` est descriptif : ID associé, tentative ou absence de tentative connue.
Ce n’est pas un statut certifiant un succès. Les colonnes facultatives absentes sont
signalées ; un historique de tentatives absent est « unknown ». Un poster peut être
absent pour une erreur réseau, un délai ou un fichier manquant, même avec un bon ID.
Une identité TMDB sans type conserve son ambiguïté ; le type d’une autre entity n’est
pas attribué à un ID release différent.

## Import et annotation des données réelles

```sh
python3 tools/parser-benchmark/benchmark.py import \
  --input tools/parser-benchmark/local/export-assaini.json \
  --output tools/parser-benchmark/local/corpus.json
python3 tools/parser-benchmark/benchmark.py references \
  --corpus tools/parser-benchmark/local/corpus.json \
  --output tools/parser-benchmark/local/references.json
```

Toutes les références réelles créées sont **unreviewed**, même avec poster/ID.
Faire revoir un sous-ensemble réparti entre catégories, sources, anciennetés et
présence/absence de poster. Conserver `ambiguous` pour les cas indécidables.
Ne pas fabriquer une attente depuis le résultat courant ou depuis le poster.

Chaque annotation contient `id`, `status`, `reviewer`, `evidence`, `parserExpected`
et `associationExpected`. Pour valider : `status="validated"`, reviewer/evidence
non vides, et au moins un champ réellement vérifié. Les attentes sont partielles :
un champ absent n’est pas noté, un champ explicitement NULL l’est. `parserExpected`
accepte les dix champs ParsedTitle. `associationExpected` accepte tmdbId, tmdbType,
tvdbId, provider, providerId et manualMatch. Vérifier une identité provider nécessite
une preuve distincte ; pas de campagne de requêtes providers par cet outil.

Le SHA-256 lie les annotations au corpus complet. Un corpus changé exige une nouvelle
revue/transposition explicite des annotations. Les métriques de parsing et d’association
ont des dénominateurs séparés. Les cas non revus ou ambigus sont exclus des scores.
« Exact sur champs annotés » n’est pas une exactitude sur les champs non annotés.

## Exécution, comparaison et sorties

`run` accepte un corpus sans références : il fournit alors des statistiques
descriptives et **aucune précision**. Ajouter `--references` après revue.

```sh
python3 tools/parser-benchmark/benchmark.py run \
  --corpus tools/parser-benchmark/local/corpus.json \
  --references tools/parser-benchmark/local/references.json \
  --out tools/parser-benchmark/local/version-a --iterations 5 --warmup 2
```

`--source-root /chemin/autre-checkout-feedarr` compile le parser d’un autre checkout
compatible avec ParsedTitle. L’outil ne fait aucun checkout ni changement Git. Chaque
exécution compile dans un dossier temporaire distinct pour éviter les collisions de
DLL entre versions. Aucun paquet NuGet externe n’est requis : sources NuGet vides,
restauration et build Release avec le SDK déjà installé. Aucune requête réseau.

Sorties : `input.json` canonique, `results.json`, `report.md`, `performance.json`.
Le JSON/Markdown déterministe sépare résultats bruts, attentes, écarts, associations
stockées et signaux descriptifs. Les exemples sont limités et issus du corpus filtré.
Le SHA-256 des sources parser/catégories identifie le code mesuré, sans chemin privé.
Le corpus et les références sont inclus pour rendre le résultat vérifiable hors ligne.

La performance est séparée car non déterministe : médiane/p95 en µs par appel,
médiane par entrée, somme des temps de parsing, répétitions, warmups, runtime et
architecture. Compilation, lecture du corpus et warmup sont exclus du temps de
parsing. `loopWallMilliseconds` inclut aussi les vérifications de répétabilité ;
ne pas le confondre avec la somme des durées du parser. Mesures mono-thread,
cache chaud, sensibles à la charge/JIT/GC ; aucune extrapolation directe au NAS.

```sh
python3 tools/parser-benchmark/benchmark.py compare \
  --before tools/parser-benchmark/local/version-a/results.json \
  --after tools/parser-benchmark/local/version-b/results.json \
  --out tools/parser-benchmark/local/comparison
```

La comparaison exige le même corpus **et les mêmes références**, ignore les temps
et recalcule les écarts plutôt que faire confiance aux scores enregistrés. Un
changement non annoté est une observation, pas une régression confirmée.
`--fail-on-change` peut rendre toute différence bloquante explicitement.

Codes de retour : 0 = opération réussie/conformité, 1 = écart validé (`check`) ou
régression validée (`compare`), 2 = données/contexte invalides ou indisponibles.
`check` refuse aussi un résultat sans aucune référence validée. Les erreurs ne
réaffichent ni titres, ni chemins, ni valeurs SQL sensibles.
