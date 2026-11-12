# V2.3-C — benchmark réel d'identification offline

**4 872 releases admissibles rejouées. Aucun gain de matching ni taux de précision ne peut être établi : aucune réponse de recherche provider et aucune référence d'identité validée ne sont disponibles.** Le benchmark mesure les préparations de requêtes, les différences de parsing et les protections, pas la réussite d'une identification externe.

## 1. Population et sécurité

La copie SQLite contient **4 904 releases**. L'exporteur V2.1 est réutilisé en mode local autonome, lecture seule `mode=ro&immutable=1`, `query_only` et authorizer restrictif. Le nouveau mode complet parcourt toute la population, sans échantillonnage : plafond explicite de 10 000 lignes avec refus au-delà, aucune troncature silencieuse.

Filtres inchangés : 11 releases exclues pour présence d'un nom de source, 21 sensibles/invalides ; **4 872 admissibles**, dont **4 529 avec référence de poster et 343 sans**. Les références SQL de posters ne prouvent ni l'existence du fichier ni la bonne identité.

| Catégorie persistée | Population admissible | Avec poster | Sans poster | Releases avec propositions non protégées |
| --- | ---: | ---: | ---: | ---: |
| Film | 1 395 | 1 323 | 72 | 72 |
| Série | 1 395 | 1 288 | 107 | 112 |
| Animation | 517 | 489 | 28 | 28 |
| Émission | 598 | 567 | 31 | 78 |
| Jeux PC | 718 | 642 | 76 | 127 |
| Spectacle | 249 | 220 | 29 | 29 |
| **Total** | **4 872** | **4 529** | **343** | **446** |

Aucune ligne n'est catégorisée Anime, Audio, Book ou Comic dans cet export ; aucune conclusion sur leur couverture réelle. Les catégories sont des observations, pas des références validées.

Base inchangée, SHA-256 : `23a3d0a355958f2eec8ed562c524282c5b5259a5ff70060991e23bedda7b159d`. Corpus complet canonique : `872ceb62c15f936671e8ce38d3081eec3d0504c22713c2288bb1d0e18508fee8`. Aucun NAS, Docker, API host, provider externe ou traitement de données persistées. Aucun paramètre/intégration/credential exporté, aucun fichier poster ouvert.

## 2. Historique réellement réutilisé

L'adaptateur appelle les helpers statiques réels de `PosterFetchService` (CleanTitle et QuerySanitizeGame), les normaliseurs et évaluateurs d'ambiguïté existants. Le corps réel de `GetTmdbCandidatesAsync` est extrait automatiquement et compilé avec un sink de requêtes en mémoire : aucune requête HTTP. Ses listes vides servent uniquement au contrôle de flux du plan ; elles ne deviennent jamais des réponses observées ou des preuves de rejet.

Pour les fixtures explicitement synthétiques seulement, l'adaptateur réutilise `ScoreCandidates`, `TmdbMatchPolicy.Evaluate`, les seuils réels de `PosterProviderSelector` et le bloc de sélection TMDB extrait du service. Seule la boucle de journalisation est omise. Les changements incompatibles du code source provoquent un refus ; les sources et outils mesurés sont empreintés.

Règles observées dans le chemin courant :

- La recherche démarre du TitleClean et de l'année **persistés**, pas d'un nouveau parsing du brut. Nettoyage terminal et accents sont traités par CleanTitle.
- TMDB essaie les types et variantes d'année de la fonction réelle ; Émission impose le plan TV. Pour les séries, TVMaze peut précéder TMDB selon configuration et résultat.
- Jeux : QuerySanitizeGame prépare la requête partagée IGDB/RAWG ; activation des providers et fallback restent conditionnels.
- Le scorer, la politique de cohérence et les seuils historiques sont distincts ; le chemin TMDB courant peut privilégier un candidat accepté ayant un poster pour choisir l'identité effectivement utilisée.
- Les corrections manuelles sont prises en charge avant la recherche normale ; cache, réutilisation d'ID et fichiers peuvent court-circuiter les appels.

**Limite de simulation :** le service complet n'est pas instancié. Cache, configuration, présence physique des posters, client matching RAWG/IGDB/TVMaze et branche de refresh manuel ne sont pas exécutés. Les **14 282 requêtes de plan** enregistrées sont conditionnelles à une recherche fraîche, pas des appels réellement effectués ni une estimation du trafic. Le résultat du helper d'ordre des providers est explicitement distingué du routing complet.

## 3. Comparaison historique / V2

Tous les bruts sont reparsés ; les dix champs sont conservés. Les 2 000 sorties déjà validées en V2.2-B restent **strictement identiques**. Aucun parser ni service de production n'a été modifié.

Pour chaque release, deux vues V2 sont produites : propositions admissibles après protection, et propositions **shadow** sans protection à des fins exclusivement diagnostiques. Ces dernières ne peuvent pas être appliquées à une association.

- **446 releases** donnent chacune un candidat admissible, soit **446 candidats**.
- La vue shadow couvre les 4 872 bruts, avec **zéro variante supplémentaire** sur ce corpus. La règle numérique prudente de V2.3-A n'y est donc pas exercée empiriquement comme deuxième interprétation.
- **864 titres de requête principaux shadow** diffèrent de la préparation historique, après nettoyage jeu réel inclus. Différences de format, accents, édition ou parsing : aucun label automatique d'amélioration.
- **842 TitleClean** et **714 années** diffèrent du stockage. Les ensembles se recouvrent ; une donnée persistée n'est pas une référence attendue.
- **512 signalements d'ambiguïté historiques**, issus de l'évaluateur réel ; ce ne sont pas 512 mauvaises associations ni 512 ambiguïtés humaines validées.

| Catégorie | TitleClean différent du stockage | Année différente | Requête principale différente | Signal historique d'ambiguïté |
| --- | ---: | ---: | ---: | ---: |
| Film | 197 | 180 | 199 | 19 |
| Série | 179 | 309 | 180 | 323 |
| Animation | 50 | 61 | 58 | 9 |
| Émission | 89 | 99 | 89 | 68 |
| Jeux PC | 296 | 36 | 305 | 93 |
| Spectacle | 31 | 29 | 33 | 0 |

Ces métriques ne comparent pas un nombre de posters trouvés. Elles ne mesurent pas encore la qualité des identités.

## 4. Identités, verrous et cas sensibles

- **4 426 releases ont au moins un identifiant externe stocké**. Elles sont protégées conservativement par la simulation, même lorsque sa fiabilité n'est pas prouvée. Le flag de protection ne transforme pas l'ID en vérité terrain et aucun TrustedIdentityHint n'en est déduit.
- **33 corrections/verrous manuels observés**, inclus dans ces protections. La détection inclut aussi le garde-fou historique des posters manuels sans révision ; les valeurs brutes de révision et chemins ne sont pas exportées.
- **3 919 références TMDB ont un type effectif absent** dans la projection release/entity. Aucun namespace n'est choisi arbitrairement à partir d'une catégorie.
- **889 releases protégées** ont un écart de titre ou d'année entre parsing et stockage : risque à revoir avant toute réassociation, pas preuve d'identité incorrecte.
- **Zéro proposition de remplacement émise** pour les identités protégées. Cela vérifie le garde-fou de la simulation ; cela ne valide pas les associations existantes.

Les types historiques incomplets, les années de saison/édition et les métadonnées possiblement anciennes restent à examiner. Aucun rebind, téléchargement ou nettoyage global n'est préparé ou lancé.

## 5. Réponses manquantes et vérité terrain

Aucune liste de résultats provider enregistrée n'est disponible. Les snapshots/IDs persistés ne sont pas utilisés comme réponses de recherche reconstituées.

- 446 lignes : classement **non évalué**, réponses absentes.
- 4 426 lignes : retour du **garde de préservation uniquement**, pas une décision d'identification.
- Acceptations, ambiguïtés et rejets d'identification réels : **indisponibles** (`null`), et non « zéro échec ».
- Différences de décisions historique/V2 réelles, gain de matching et précision : **non mesurables**.

Les **120 cas de revue initiaux** sont repris dans un nouveau formulaire privé. Les **18 références lexicales partielles** déjà validées (titre/année) sont conservées avec leur preuve ; les 102 autres restent non validées. **Aucune identité provider validée**. Chaque fiche sépare parsing attendu, identité attendue, ambiguïté légitime, mauvaise association et observation persistée ; aucune précision globale n'est calculée.

L'import de captures réelles reste refusé tant que leur provenance et la couverture exacte des requêtes n'ont pas de contrat vérifiable. Le mode de réponses optionnel est réservé aux fixtures explicitement synthétiques et ne contribue à aucune métrique réelle.

## 6. Comparaison des adaptateurs sur fixtures synthétiques

| Fixture de test, distincte du corpus réel | Historique TMDB | V2.3-B |
| --- | --- | --- |
| Deux IDs, même titre et année | Accepte un résultat suivant sa sélection réelle | Ambigu, écart insuffisant |
| Film demandé / résultat série | Rejette | Rejette |
| Correction manuelle | Service de refresh non exécuté | Préserve, aucune identité choisie |
| Réponses absentes | Non évalué | Non évalué |
| Lot vide explicitement fourni | Rejette | Rejette |

Ceci vérifie la plomberie des adaptateurs et la distinction « absent / vide » ; cela ne démontre aucun gain de précision sur une œuvre réelle.

## 7. Performance et validation

.NET 8.0.33 Arm64, une passe de warmup puis trois répétitions par release, soit **14 616 mesures** : médiane **112,2 µs**, p95 **255,2 µs**, somme des appels **1 937,99 ms**. Allocations du thread courant par rejeu : médiane **67 288 octets**, p95 **109 320 octets** ; changement de thread refusé pour cette mesure.

Le temps inclut parsing, génération shadow/protégée, préparation historique et garde de ranking. Il exclut compilation et sérialisation. Ce n'est ni le coût isolé du ranker, ni une latence réseau, ni une comparaison de performance de deux pipelines de production.

**28 tests ciblés des outils réussis**, dont six nouveaux : population complète au-delà de 2 000, refus de troncature, réutilisation du code historique, protection des données, préservation des références et intégration des adaptateurs synthétiques. **Compilation Release API + runner réussie, zéro avertissement/erreur.** Aucune suite backend complète lancée. `git diff --check` propre.

## 8. Conditions avant calibration et activation

1. Capturer hors de cette étape des réponses autorisées et expurgées, liées à la requête exacte, au type, au provider et aux versions du code ; couvrir les variantes nouvelles, pas seulement la requête historique.
2. Valider manuellement les identités des 120 fiches indépendamment des posters et IDs stockés ; élargir ensuite à un échantillon représentatif. Distinguer réfutation, identité correcte, ambiguïté légitime et absence de référence.
3. Calibrer les seuils sur ces seules références validées, avec cas négatifs, concurrents proches et séparation des années parsées/hypothétiques/confirmées. Ne pas régler les seuils pour améliorer le nombre de signalements.
4. Compléter les adaptateurs de réponses providers, cache et routing ; contrôler les révisions/verrous persistés au point d'écriture. Les protections shadow ne remplacent pas ces contrôles atomiques.
5. Revoir les 3 919 scopes TMDB incomplets et les 889 écarts sur identités protégées, sans réassociation automatique. Aucune activation tant que ces garanties et les faux positifs ne sont pas mesurés.

Artefacts privés ignorés : `tools/parser-benchmark/local/v23c-complete/` (résultats, résumé, timings, formulaire 120 cas, manifestes et logs), corpus complet `v23c-corpus.json`, tests `v23c-tool-tests.log`. Reproduction : section V2.3-C du README des outils. Aucun de ces exports n'est destiné au commit.

Fichiers de ce lot : exporteur et validation de schéma existants, README, nouveau `identification.py`, `IdentificationRunner/` (projet et deux sources), tests Python ciblés et ce rapport. Composants V2.3-A/B, fichiers étrangers et données locales conservés. **Aucun commit ni push.**
