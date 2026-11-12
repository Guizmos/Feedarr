# V2.3-J — Replay ciblé et reproductible

Implémentation locale sur `beta`, après H et les modifications locales I, conservées.
Aucun commit/push. Aucun parser, ranker, seuil ou règle métier de matching modifié par J
(empreintes vérifiées avant/après, y compris `OfficialCompoundTitle`).

## Sélection et aperçu

Dans Maintenance → Matching V2 Shadow — Beta → Replay :

- **Échantillon déterministe** reste le mode par défaut, avec les strates existantes.
- Catégories : Films, Séries, Animations, Émissions, Spectacles ; sélection multiple.
- Filtre exclusif : Toutes, Sans poster, Sans identité, Avec identité.
- **IDs explicites** : 1 à 200 IDs positifs uniques, dans l’ordre indiqué ; virgules,
  espaces et points-virgules acceptés dans l’interface. Aucun ID n’est remplacé.
- **Divergences Shadow en mémoire** : releases dont une divergence figure dans la
  fenêtre actuelle, sans reconstituer un historique supprimé ni substituer des releases récentes.

Les filtres sont appliqués avant la limite du pool SQL et l’échantillonnage.
Les IDs explicites contournent ce pool, mais doivent tous être présents, admissibles
et compatibles avec les filtres : sinon la requête est rejetée avant tout HTTP.
Les références poster/identité sont évaluées à partir des champs stockés ; ce lot
ne vérifie pas les fichiers posters sur disque et ne valide pas les identités.

**Estimer sans appel provider** effectue seulement des lectures SQLite ReadOnly et
la génération V2 existante. Il n’ouvre aucun client HTTP et ne lit aucun credential.
L’aperçu montre IDs, répartition, besoin minimal de requêtes et quotas éventuels.

En ligne, le besoin minimal est celui des premières pages des requêtes V2, sans locale
explicite puis avec la locale UI. Pagination, redirections et erreurs restent inconnues.
C’est un besoin théorique de couverture, pas une promesse du nombre réellement envoyé :
absence de configuration, erreur ou arrêt peuvent réduire/annuler les appels.
Hors ligne, l’estimation et les appels restent **zéro**.

Le lancement UI exige un aperçu. Ses IDs sont transmis comme `expectedIds` : si la
sélection recalculée diffère, démarrage refusé avec 409 plutôt que remplacement silencieux.
Les données peuvent néanmoins évoluer ; leurs protections sont toujours relues.
Une seule préparation est admise à la fois, sans attente bloquante ni verrou de session
pendant SQL/parsing. L’arrêt et la génération peuvent invalider un démarrage en préparation.

## Budget équitable

Le plafond global demeure **30 tentatives HTTP**, réglable uniquement entre 1 et 30.
L’option équitable est désactivée initialement pour préserver le mode H à budget commun.

Quand activée : partage entier égal entre les catégories **présentes dans l’échantillon**.
Le reste est distribué dans l’ordre canonique des catégories, de manière déterministe.
Chaque budget enfant est contrôlé par le budget parent global, au niveau du handler
HTTP interne : erreurs et redirections consomment les deux quotas.

Une catégorie ne peut pas emprunter le budget d’une autre. Les parts inutilisées ne
sont pas redistribuées automatiquement. Si une release ne peut même pas couvrir ses
premières pages/locales/variantes dans le quota restant, aucun appel inutile n’est lancé.
Si une pagination imprévue épuise le quota, la release devient `not_evaluable` : aucun
résultat partiel n’est accepté. L’estimation peut donc être dépassée sans dépasser un quota.
Une petite enveloppe peut laisser toutes les releases non évaluables ; l’interface
l’annonce, sans masquer ce manque de couverture ni le présenter comme un défaut de matching.

Timeouts, Retry-After entre sessions, destination TMDB Search stricte, annulation,
concurrence HTTP maximale de 1 et refus des publications tardives sont conservés.

## Rejouer et exporter

**Reprendre les mêmes IDs sélectionnés** recharge exactement l’ordre des IDs de la
session précédente en mode explicite, remet le filtre à Toutes et autorise les cinq
catégories. Choisir ensuite mode/budget, recalculer l’aperçu et lancer. Un ID supprimé
ou devenu inadmissible fait échouer la sélection ; aucun autre ID ne le remplace.
Après redémarrage, les IDs exportés peuvent être recopiés dans la saisie multiple.

Le rapport JSON version 2 ajoute :

- paramètres normalisés de sélection, mode et budget ;
- `selectedIds` figés, distincts des `processedIds` effectivement publiés ;
- quotas, besoins minimaux et consommation par catégorie ;
- empreinte du plan de requêtes et locale connue ;
- empreinte/version de la projection provider utile au matching et indicateur de couverture complète.

Un arrêt conserve les IDs sélectionnés, mais ne transforme pas une release annulée
en release traitée. Rejouer les IDs ne fige ni leurs métadonnées actuelles ni les réponses
futures du provider. Les corrections et associations restent des données protégées,
jamais une vérité terrain automatiquement validée.

## Comparaison de deux exports, entièrement hors ligne

La section **Comparer deux rapports JSON hors ligne** lit deux fichiers dans le navigateur,
sans upload ni appel provider. Limite : 10 Mo et 200 entrées par rapport ; schémas, IDs,
décisions, codes de couverture, locales et empreintes sont validés. Les titres, payloads
bruts et champs inconnus d’un fichier importé ne sont ni projetés ni affichés.

Comparaison par ID de release : présence/absence, décision, identité proposée, couverture,
puis variation de la projection provider. Les empreintes excluent scores/décisions et
sont stables face à l’ordre des résultats/doublons ; elles couvrent titres officiels,
identités typées, année et données structurées utiles. Elles n’exportent aucun corps HTTP,
URL, poster, credential ou détail d’indexeur.

Une variation provider n’est déclarée comparable que si couverture complète, plan,
locale et version de projection sont connus et compatibles. Sinon : preuve absente,
couverture incomplète, contexte inconnu/différent ou version différente. Les rapports H
restent lisibles, mais sans empreintes on ne fabrique aucune comparaison provider.
Une différence de décision seule n’est ni une variation provider ni une mesure de précision.

Limites : comparaison de **projections filtrées**, pas des réponses brutes ; les champs
non pertinents (posters, ordre), masqués ou non conservés ne sont pas comparés. La section
ne valide aucune identité et ne mesure pas les faux positifs sans références indépendantes.

## API et sécurité

Routes existantes conservées ; ajout :

`POST /api/diagnostics/matching-shadow/replay/preview`

Même corps que le démarrage, sans `expectedIds` obligatoire :

```json
{
  "count": 10,
  "mode": "offline",
  "budget": 30,
  "generation": 0,
  "selection": {
    "method": "sample",
    "categories": ["films", "series"],
    "releaseIds": [],
    "filter": "without_poster",
    "fairBudget": true
  }
}
```

Utiliser la génération courante de GET ; pour lancer la sélection prévisualisée,
ajouter les `selectedIds` reçus comme `expectedIds` et garder cette génération.
`method` vaut `sample`, `ids` ou `shadow_divergences` ; IDs requis uniquement pour `ids`.

Auth/anti-CSRF existants, tokens bootstrap refusés, `no-store` conservé.
Corps limité à 16 Ko ; catégories, filtres, méthodes, cardinalités, doublons et entiers
sûrs JS sont validés. SQL paramétré ; lectures exclusivement ReadOnly/query_only.
Erreurs de lecture SQLite rendues par code fixe 503, sans détail interne.
Aucune écriture métier, poster, métadonnée, association, cache ou statistique de synchronisation.
Les sessions restent bornées en mémoire, sans reprise automatique ni historique permanent.
Les exports contiennent encore des titres/IDs privés : les conserver hors Git.

## Fichiers J et validation

- Backend : `MatchingReplayReader`, `MatchingReplayService`, `ReplayHttpBudget`,
  `MatchingReplayController`, nouveaux `ReplaySelection` et `ReplayEvidence`.
- Frontend : panneau Replay, modèle de sélection, nouveaux composant/modèle de comparaison.
- Tests : `MatchingReplaySelectionTests.cs` (36 nouveaux cas), passage en classe partielle
  de la fixture H et `matchingReplaySelection.test.mjs` (11 nouveaux cas).
- Cette documentation. Modifications locales I et fichiers étrangers préservés.

Résultats :

- **267 tests ciblés** réussis, couvrant H/I/J et les contrats vidéo existants.
- **17 tests frontend** réussis.
- Builds solution Release et Vite réussis ; Release sans avertissement/erreur.
- Lint ciblé **et `npm run lint` complet** réussis.
- **1 958 tests backend Release réussis**, suite complète exécutée une seule fois.
- `git diff --check` propre ; parser/ranker/I inchangés par J, vérifiés par empreintes.
- SQLite privée du benchmark inchangée :
  `23a3d0a355958f2eec8ed562c524282c5b5259a5ff70060991e23bedda7b159d`.

Tests uniquement sur clients simulés, bases SQLite et dossiers temporaires. Aucun
provider réel, NAS, Docker, déploiement, commit ou push pendant ce lot.
