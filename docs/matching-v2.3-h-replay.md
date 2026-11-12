# V2.3-H — Replay contrôlé du matching V2

Implémentation locale sur `beta`, après `6a78395`. Aucun commit/push ni appel provider réel pendant le développement.

## Architecture et isolation

Le replay est un service singleton **distinct du pipeline de production**. Une seule
session peut travailler à la fois, avec au maximum 200 releases et 30 tentatives HTTP.
Aucun démarrage automatique. Une nouvelle instance serveur commence sans session.
Le Shadow passif reste OFF par défaut ; un replay explicitement demandé peut fonctionner
sans l’activer et ne remplit pas ses compteurs.

- `MatchingReplayReader` lit l’échantillon, les états courants et la configuration TMDB
  de l’application via `MatchingReadOnly` : SQLite `ReadOnly`, `query_only`, aucune
  migration, aucun `Db.Open()` en lecture-écriture.
- `MatchingReplayService` exécute le générateur et le ranker existants, sans modifier
  leurs seuils ni le parser. Les IDs enregistrés ne deviennent ni requêtes ni vérité terrain.
- `TmdbClient.SearchReplayPageAsync` réutilise les DTOs et le décodage TMDB, **pas**
  `GetJsonAsync`, qui écrit des statistiques provider.
- Le client HTTP dédié réutilise le transport public validé : SSRF strict, validation
  des connexions et redirections. Aucun retry automatique, circuit breaker/statistiques
  métier ou journal de requêtes contenant l’API key. Seuls les endpoints Search movie/tv
  HTTPS du domaine TMDB sont autorisés ; les chemins posters et autres destinations sont refusés.
- `ReplayBudgetHandler`, placé au plus près du transport, compte chaque tentative,
  y compris échecs et redirections. Budget validé deux fois, limité à 1–30 ; envoi
  séquentiel, intervalle minimal de 250 ms et timeout de 20 s par requête/lecture du corps.

Les lectures et appels HTTP n’ont jamais lieu sous le verrou du service. L’arrêt/reset
invalide la génération avant annulation ; une tâche ou activation tardive ne peut pas
publier. Un nouveau démarrage est refusé tant que le worker précédent ne s’est pas
terminé, même après reset. Une requête déjà engagée reste comptée ; ses résultats
après arrêt sont abandonnés.

Les corrections/identités protégées peuvent être **inspectées** par ce replay explicite.
Le ranker produit une proposition diagnostique, marquée `PreserveExisting` ; aucun
writer de releases, entités, associations, métadonnées, posters, caches ou statistiques
n’est accessible au service. Les états persistés sont relus avant et après évaluation :
un changement concurrent invalide la proposition. Le comportement conservateur du
Shadow passif sur les identités protégées est conservé.

## Échantillon

Lecture d’un pool plafonné à 5 000 lignes admissibles, sélectionné avec un ordre SQL
stable par strate puis hash d’ID, jamais un simple tri des dernières releases.
Sélection circulaire par catégorie, puis par source/présence de poster/identité.
À l’intérieur des strates : priorité aux absences de poster/identité, ambiguïtés
signalées par le composant existant et indices lexicaux français. Ces indices de
sélection ne prouvent pas la langue et ne modifient aucune règle de matching.

Catégories admises : films, séries, émissions, spectacles et animation, avec un type
TMDB non ambigu dans les mappings existants. Anime, jeux et autres catégories sont
exclus ; aucun type film/série n’est inventé pour eux. Les titres absents ou dépassant
4 096 caractères sont exclus plutôt que tronqués. La répartition réelle apparaît
après sélection ; un petit catalogue peut fournir moins de releases que demandé.
Le pool est borné en retour ; son calcul SQL peut toutefois parcourir le catalogue.

## Hors ligne

Aucun client HTTP n’est créé. Le replay fige au départ la fenêtre Shadow locale,
réutilise seulement une observation complète dont l’état d’entrée correspond exactement
à la release actuelle, puis reclasse ces candidats avec V2.

Sans cette preuve locale : `offline_provider_responses_unavailable`, donc
`not_evaluable`. Les identités, métadonnées ou posters stockés seuls ne permettent pas
de reconstruire une réponse provider. Au démarrage, la fenêtre Shadow est vide ;
beaucoup ou toutes les releases peuvent donc être non évaluables hors ligne.
Les anciens rapports de replay ne constituent pas un cache provider réutilisable.

## Recherche TMDB

Nécessite une instance TMDB déjà activée et configurée dans l’application. Son API key
est lue/déchiffrée en mémoire, sans être affichée, exportée ou journalisée. La copie
SQLite privée du benchmark n’est jamais utilisée par le service.

Pour chaque requête V2 : Search dans le type connu, sans locale explicite puis avec
la locale UI réellement configurée ; pagination si nécessaire. Les titres localisés
et originaux réellement reçus sont traités avec l’adaptateur V2.3-G partagé. Aucun
appel de traduction, de titres alternatifs, d’images, de détails ou autre provider.
Les titres contenant une URL ou un credential apparent sont refusés avant envoi.

Toutes les pages/locales/variantes nécessaires doivent être couvertes avant classement.
Budget épuisé, pagination incohérente/dupliquée, réponse absente/invalide/partielle,
timeout ou plus de 100 lignes provider : `not_evaluable`, sans acceptation d’un résultat
tronqué. Un résultat vide complet reste distinct d’une réponse absente.

Après un 429 : pas de nouvelle recherche dans la session, respect de `Retry-After`
entre sessions/reset (60 s si absent). Une erreur d’authentification arrête les appels
de la session. Les autres erreurs comptent dans le budget et sont exposées par codes
fixes, jamais par texte d’exception.

Les réponses Search ne fournissent pas nécessairement saisons/épisodes ou toutes les
traductions : une couverture HTTP complète n’est pas une identité validée. Les
hypothèses d’année restent hypothétiques.

## Rapport, API et confidentialité

Panneau repliable dans **Paramètres → Maintenance → Matching V2 Shadow — Beta** :
10/25/50/100/200 releases, modes Hors ligne/Recherche TMDB, budget, progression,
erreurs, résumé, inspection, arrêt/reset et export JSON.

API :

- `GET /api/diagnostics/matching-shadow/replay`
- `POST` sur cette route avec `{ "count": 10, "mode": "offline", "budget": 30, "generation": G }`
- `POST .../stop`, `POST .../reset`
- `GET .../export`

G est la génération retournée par GET ; un démarrage périmé est refusé. Les routes
héritent de l’authentification et de l’anti-CSRF existants, refusent les tokens de
bootstrap d’installation et renvoient `Cache-Control: no-store`. La politique d’accès
configurée de Feedarr reste inchangée.

Une identité TMDB historique n’est affichée que si son type est connu. Sans décision
historique réellement observée, la différence de décision reste **inconnue** ; une
identité stockée n’est pas transformée en ancienne décision « accept ». Une différence
d’identité est calculée seulement si les deux identités typées sont disponibles.
`IdentityVerified` reste toujours `unknown`, même pour une correction manuelle.
Les compteurs ne représentent aucune précision mesurée.

La projection commune limite/filtre les textes et ne transporte ni source/indexeur,
URL, credential, poster URL, exception ni réponse brute. L’export est le même DTO
filtré que l’interface, téléchargé par le navigateur ; aucun fichier de rapport n’est
écrit sur le serveur. Il contient néanmoins les titres/IDs sélectionnés : conserver
cet export comme donnée privée, hors Git. La session complète est bornée à 200 entrées,
en mémoire seulement ; pas d’historique permanent. Les compteurs Shadow passifs et
ceux du replay sont séparés.

## Validation

- 181 tests ciblés réussis, puis **36 tests replay** réussis après le dernier garde-fou
  de confidentialité (échantillonnage, offline, budget/erreurs/redirections, pagination,
  rate limits, concurrence, arrêt/reset, redémarrage, multilingue, protections et export).
- **1 873 tests backend réussis en Release**, suite complète exécutée une seule fois.
- **6 tests frontend ciblés** réussis : limites, différence inconnue, rendu du replay,
  contrôles et échappement des détails Shadow.
- Build solution Release réussi : 0 avertissement, 0 erreur. Build Vite et lint ciblé verts.
- `git diff --check` propre.
- Tests sur SQLite/dossiers temporaires : fichiers de base, WAL et posters inchangés
  par le replay. Le SHM de coordination SQLite est exclu de la comparaison binaire.
- SQLite privée du benchmark : SHA-256 inchangé
  `23a3d0a355958f2eec8ed562c524282c5b5259a5ff70060991e23bedda7b159d`.

Fichiers principaux : lecteur et service sous `Services/Matching/Replay/`, budget HTTP,
`TmdbClient.Replay.cs`, contrôleur, `MatchingReadOnly.cs`, adaptateur de titres partagé,
composants/modèle React, tests et wiring DI. Les hooks Shadow réutilisent le lecteur
ReadOnly et la projection commune ; aucun changement du moteur historique.

## Essai contrôlé en Beta après revue/livraison

1. Ouvrir Maintenance, développer **Rejouer le matching — diagnostic contrôlé**.
2. Choisir **10 / Hors ligne**, lancer, inspecter répartition et motifs non évaluables.
3. Si TMDB est **déjà configuré dans la Beta**, choisir **Recherche TMDB**, 10 releases,
   budget **4** pour le premier essai explicite. Le budget peut rendre la majorité
   des releases non évaluables ; cela ne signifie pas qu’elles sont mal associées.
4. Tester **Arrêter le replay** pendant un appel : aucune publication tardive ; attendre
   la fin d’annulation avant un nouveau départ. **Effacer la session** ne contourne
   pas un cooldown provider.
5. Inspecter les preuves et les différences inconnues, exporter le JSON filtré et
   vérifier indépendamment les identités avant toute conclusion. Aucun bouton ne
   permet d’appliquer une proposition aux données métier.

Aucun NAS, Docker, déploiement, rematching global ou modification de configuration
production effectué. Pas de commit/push automatique.
