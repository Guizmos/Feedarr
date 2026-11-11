# Lot 3 — identités TMDB et corrections manuelles

Base : `beta` au commit `90d9ae0`. Périmètre : MOT-P1-01 et MOT-P1-03.
L’audit métier existant est réutilisé, notamment ses reproductions conservées dans
`/private/tmp/feedarr-business-probe.log` : déduplication movie/123 + series/123
réduite à un candidat, fichiers et stores manuels communs à movie/100 et series/100,
et cache remplaçant une identité lors d’un refresh forcé. Aucun nouvel audit général.

## Identités et stockage

- `TmdbIdentity` représente `tmdb:movie:id` ou `tmdb:tv:id` ; les alias film/movie et
  series/tv/show sont normalisés. Déduplication automatique et recherche manuelle
  conservent les deux types à numéro identique. Les recherches automatiques de type
  connu ne sélectionnent plus un candidat de l’autre type.
- Les nouveaux fichiers sont `tmdb-movie-id-*` / `tmdb-tv-id-*`, les stores
  `tmdb-movie-id` / `tmdb-tv-id`, les clés `tmdb:movie:id` / `tmdb:tv:id`.
  Fanart distingue également les namespaces movie/TV dans ses fichiers et stores.
- Les fingerprints vidéo sont versionnés et typés. Les IDs JSON acceptent encore
  l’ancien format, mais un numéro TMDB sans type ne prouve plus un recouvrement.
  TVMaze, RAWG et IGDB conservent leurs namespaces propres. Un identifiant IGDB
  n’est plus enregistré comme identifiant TMDB ; le contrat de test est ajusté.
- Métadonnées, rebind canonique et associations Arr utilisent le type explicite.
  Les caches propres à un seul type (ex. recherche TV par TVDB, Radarr uniquement
  films) conservent leur séparation implicite par fournisseur/endpoint.

## Correction manuelle et concurrence

La sélection manuelle existante transmet maintenant le type du candidat. Le backend
conserve la compatibilité des anciens clients lorsque la release fournit déjà un
type non ambigu. La correction est persistée sur l’entity : fournisseur, type,
identifiant et révision aléatoire. Toutes ses releases reçoivent la même identité,
et les métadonnées, IDs secondaires, statut Arr et références de poster antérieurs
sont invalidés dans la même transaction. Aucun ancien fichier n’est supprimé.
Les caches sont invalidés par titres/types/années concernés ; le cache d’une série
homonyme reste intact après correction d’un film.

Le matching automatique capture entity + révision avant les appels réseau. Chaque
publication en base revalide ces valeurs sous transaction SQLite ; un worker parti
avant correction, changement ou retrait du verrou est rejeté. Les backfills de
métadonnées et d’identifiants capturent aussi leur révision. La matérialisation de
stores vérifie la révision sous le verrou de store avant de publier des fichiers.
Les sélections manuelles ont des noms de fichiers uniques et des stores réécrits
explicitement : deux téléchargements ne partagent pas un fichier manuel provisoire.
Une image invalide est rejetée avant de modifier l’identité précédente.

Un refresh normal ou forcé d’un match verrouillé ignore entièrement le matching et
ses caches, enrichit la même identité TMDB et ne remplace pas un poster manuel présent.
Si le poster manque, seule cette identité est sollicitée. Les anciens marqueurs de
poster manuel sans identité typée restent conservés sans tentative de deviner le type.

Un nouveau choix manuel remplace explicitement la correction. Le renommage manuel
existant abandonne l’identité et les métadonnées de l’ancien titre puis invalide les
caches concernés ; les anciens workers ne peuvent pas republier. L’API
`DELETE /api/posters/release/{id}/manual` retire le verrou pour l’entity liée, change
sa révision et invalide les caches concernés. Elle ne lance aucun refresh ; une
actualisation ultérieure peut à nouveau effectuer le matching automatique.
Les rebinds automatiques ignorent les entities verrouillées.

## Historique et migration

`0057_matching_identity.sql` ajoute uniquement des colonnes nullable aux tables
releases et media_entities. Aucune identité historique n’est déduite ou réécrite.
Les anciennes migrations 0055 Backup V2 et 0056 GC restent intactes.
Les fichiers/stores non typés demeurent lisibles sur leurs références existantes,
mais ne sont ni recopiés dans un namespace supposé correct ni réutilisés pour une
nouvelle association. Le fallback de cache refuse les anciens fichiers TMDB/Fanart
non typés ; les anciennes lignes de cache restent présentes et protégées.
Les associations historiques mélangeant plusieurs types sont protégées, pas
corrigées arbitrairement. Un ID historique sans type identifiable ne déclenche pas
un essai movie puis TV. Un donneur de métadonnées dont l’ID externe ne correspond
pas à l’identité TMDB ne peut plus propager ses détails.

Seule adaptation GC : reconnaître la nouvelle colonne automatique `tmdb_media_type`
dans sa liste de colonnes connues. Sans cela, le GC classerait toutes les nouvelles
entities typées comme données inconnues ; aucun critère de purge n’est élargi.
Les traces de correction manuelle et sa provenance restent protégées.

Aucune association massive, purge, tâche de rafraîchissement ou téléchargement
supplémentaire n’est déclenché au démarrage. Backup V2, wizard, Docker et Synology
ne sont pas modifiés. Le script local non suivi `feedarr-poster-storage-audit.py`
est préservé. Tous les tests utilisent des bases et dossiers temporaires.

## Validation finale

- 264 tests backend ciblés verts ; contrôle complémentaire recherche manuelle :
  55 tests verts (ensembles recouvrants, ne pas additionner).
- Suite backend complète exécutée une seule fois : **1 213 réussites**, zéro échec
  ou test ignoré ; résultat TRX dans `/private/tmp/feedarr-lot3-results`.
- Le test de migration reconstitue le schéma antérieur sur SQLite temporaire avec
  release/entity, cache et référence à un ancien store partagé ; application 0057
  puis redémarrage du runner, données et octets conservés, integrity/FKs OK.
- Collisions movie/TV (y compris recherche manuelle et titres homonymes), caches
  distincts, posters/stores séparés, refresh normal/forcé, cache obsolète encore
  présent, vrai worker suspendu puis repris après correction, releases partageant
  une entity, changement/retrait/renommage, métadonnées obsolètes de même numéro
  et autre type, image manuelle invalide, historique ambigu/mixte couverts.
- Régressions TVMaze, RAWG, IGDB, backfills, Arr et rétention incluses.
- Frontend ciblé bibliothèque : 50/50 ; ESLint du composant et Vite verts.
- Compilation backend Release : zéro avertissement, zéro erreur.
- `git diff --check` vert ; aucun changement Backup V2/wizard.

## Fichiers modifiés

- `docs/tmdb-manual-matching-lot3.md`
- `src/Feedarr.Api.Tests/GameMatchingStrategyContractTests.cs`
- `src/Feedarr.Api.Tests/Golden/game-contract-expected.json`
- `src/Feedarr.Api.Tests/Golden/video-contract-expected.json`
- `src/Feedarr.Api.Tests/MatchingIdentityTests.cs`
- `src/Feedarr.Api.Tests/PosterFetchServiceProviderLimiterTests.cs`
- `src/Feedarr.Api.Tests/PosterFetchStoreMaterializationTests.cs`
- `src/Feedarr.Api.Tests/PosterMatchingContractTestRig.cs`
- `src/Feedarr.Api/Controllers/MaintenanceController.cs`
- `src/Feedarr.Api/Controllers/PostersController.cs`
- `src/Feedarr.Api/Data/Migrations/0057_matching_identity.sql`
- `src/Feedarr.Api/Data/Repositories/MediaEntityRepository.cs`
- `src/Feedarr.Api/Data/Repositories/ReleaseRepository.HistoryRetention.cs`
- `src/Feedarr.Api/Data/Repositories/ReleaseRepository.MatchingIdentity.cs`
- `src/Feedarr.Api/Data/Repositories/ReleaseRepository.cs`
- `src/Feedarr.Api/Models/PosterMatchIds.cs`
- `src/Feedarr.Api/Models/ReleaseForPoster.cs`
- `src/Feedarr.Api/Services/Arr/MediaEntityArrStatusService.cs`
- `src/Feedarr.Api/Services/Matching/TmdbIdentity.cs`
- `src/Feedarr.Api/Services/Metadata/ExternalIdBackfillService.cs`
- `src/Feedarr.Api/Services/Metadata/RequestTmdbResolverService.cs`
- `src/Feedarr.Api/Services/Metadata/TmdbMetadataBackfillService.cs`
- `src/Feedarr.Api/Services/Posters/PosterFetchService.ManualIdentity.cs`
- `src/Feedarr.Api/Services/Posters/PosterFetchService.cs`
- `src/Feedarr.Api/Services/Posters/PosterMatchCacheService.cs`
- `src/Feedarr.Web/feedarr-web/src/pages/library/Library.jsx`
