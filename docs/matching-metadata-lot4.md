# Lot 4 — précision du matching et snapshots de métadonnées

Base : `beta` au commit `99f201d`. Périmètre : MOT-P1-02 et MOT-P2-05.
Les conclusions de l’audit métier existant sont réutilisées : son probe acceptait
un film d’année 1990 pour une requête 2012 avec un score 0,96, et montrait une ligne
RAWG/222 conservant un ancien résumé TMDB. Aucun nouvel audit général.

## Causes et règles de sélection

`TmdbMatchPolicy` laissait le score fort court-circuiter les contradictions, alors
que seuls les matchs faibles vérifiaient type et année. Le chemin réel ne testait
que le premier candidat : rejeter ce candidat pouvait aussi masquer un suivant
valide. Les candidats avec/sans image pouvaient ensuite fournir des IDs différents.

La politique évalue désormais les contradictions avant les seuils de confiance :

- Type film/TV connu et contradictoire : refus, quel que soit le score.
- Année inconnue d’un côté : absence d’information, pas contradiction ; les règles
  de confiance et d’ambiguïté existantes restent applicables.
- Années identiques : règles de confiance existantes, y compris le match faible
  avec titre commun significatif et année exacte.
- Années voisines (±1) : tolérance de date de sortie internationale/première diffusion,
  avec titre exact ou soutien fort et non ambigu. Un match faible ne bénéficie pas
  de cette tolérance. Les titres communs courts gardent l’exigence d’année exacte.
- Film, ou série sans contexte d’épisode tardif : un écart supérieur est contradictoire.
- Série avec saison ultérieure, spéciale ou épisode sans saison connue : l’année
  du fichier peut être celle de diffusion de l’épisode. Une première diffusion plus
  ancienne reste possible avec titre exact et non commun ; une première diffusion
  postérieure de plusieurs années est rejetée. La saison 1 explicite ne bénéficie
  pas de cette exception. Aucun écart maximal uniforme n’est imposé aux séries longues.

La sélection classe les candidats puis choisit parmi ceux acceptés. Images, TMDB,
TVDB et détails suivent la même œuvre retenue. Le fallback Fanart ne réutilise plus
un ancien ID lorsque tous les candidats TMDB ont été refusés. La recherche TMDB de
requêtes de séries utilise aussi la politique avant de retenir un résultat de titre.
Les refus sont journalisés par code stable, numéro, type, années et score ; aucune
clé API, URL authentifiée ou réponse brute de provider n’est journalisée.

## Propriété des données et transitions

Les appels `UpdateExternalDetails` fournissent un snapshot du provider, pas un patch
utilisateur. Chaque champ dépendant du provider est remplacé, y compris par NULL :
titre externe, résumé, tagline, genres, date, durée, note, votes, réalisateurs,
auteurs et casting. Le provider, son ID et la date de mise à jour sont cohérents
avec ce snapshot. Aucun `COALESCE` ne réintroduit des données d’une autre œuvre.
Cela répare aussi les snapshots historiques hybrides lors d’un futur enrichissement.

Une transition d’identité invalide ces données avant de publier le nouveau poster
ou ID. Les IDs secondaires (TVDB, IMDb, résolution de requête TMDB), statuts Arr,
références de poster/store et audit de poster incompatibles sont invalidés dans la
même transaction. Les nouveaux IDs fournis ou résolus sont ensuite publiés pour
l’œuvre sélectionnée. Un mapping TMDB vérifié peut conserver les détails de cette
même œuvre lorsqu’un poster TVMaze/Fanart est utilisé. Un ID TMDB antérieur ne sert
plus de remplacement à un mapping TVMaze absent.

Les releases d’une entity reçoivent un snapshot cohérent ensemble. Les fichiers
images existants ne sont pas supprimés par cette invalidation de références.
Titres utilisateur, `title_manual_override`, état vu, informations de release,
provenance et verrou d’identité manuelle sont préservés.

La révision manuelle du lot 3 reste obligatoire à la publication. Le contrôle ajoute
une comparaison de l’identité externe courante : un résultat commencé avant un
changement automatique est aussi rejeté. Le scope est avancé uniquement après ses
propres écritures transactionnelles. L’endpoint d’enrichissement IGDB capture ce
scope avant ses appels réseau et renvoie 409 si la correction a changé entre-temps.
Une requête forcée ne peut pas remplacer le provider d’une correction manuelle.

## Historique et migration

`0058_metadata_snapshot_provenance.sql` ajoute `ext_snapshot_version`, nullable, aux
tables releases et media_entities. Aucune ligne historique n’est modifiée au boot.
NULL désigne un snapshot ancien non vérifié ; la version 1 est écrite après remplacement
par le nouveau chemin. Seuls les snapshots vérifiés peuvent servir de donneurs locaux
TMDB. Un enrichissement normal demandé sur une release peut réparer son ancien snapshot,
sans téléchargement de poster si celui-ci existe déjà.

Les fingerprints vidéo passent à `tmdb-v3`. Les lignes de cache anciennes restent
présentes, mais ne peuvent plus contourner la nouvelle politique via un lookup exact
ou un fallback de titre. La réutilisation entre releases vidéo exige aussi un snapshot
vérifié et une date de provider compatible. Une actualisation forcée ne passe pas par
ce raccourci. Les autres namespaces de providers restent séparés.

Les cas ambigus existants ne sont pas réassociés arbitrairement. Aucune migration ne
lance de rebind, purge, téléchargement, refresh global ou tâche nouvelle. Aucun accès
au NAS, aucun changement Docker, Backup V2, wizard ou frontend.

Seule adaptation GC : inscrire `ext_snapshot_version` dans les colonnes connues ;
sans cela, la présence de ce marqueur technique protégerait à tort toutes les nouvelles
entities enrichies. Aucun critère de rétention ou de suppression physique n’est élargi.

## Validation

- Dernière passe ciblée : **326 tests réussis**, aucun échec ou test ignoré.
- Suite complète de remplacement autorisée après interruption : **1 245 réussites**,
  zéro échec/ignoré, 58 secondes. Rapport TRX conservé dans `.artifacts/lot4-validation`.
- L’exécution globale initiale avait été lancée avant interruption, mais son processus
  et ses résultats temporaires ont disparu. Son résultat est inconnu ; elle n’est pas
  présentée comme verte. L’utilisateur a explicitement autorisé une seule exécution
  de remplacement, effectuée sur la version finale inchangée.
- Release revérifiée à la reprise : zéro avertissement, zéro erreur.
- `git diff --check` vert. Frontend non modifié : aucun test frontend lancé.
- Migration 0058 testée sur SQLite temporaire préexistant puis runner relancé :
  données et octets historiques conservés, integrity_check et foreign_key_check verts,
  réparation uniquement lors de l’enrichissement ciblé ultérieur.
- Régressions TVMaze, RAWG, IGDB, rétention, backfills et protections manuelles du lot 3
  incluses dans les tests. Aucun accès au NAS ni changement Docker.
- Backup V2, wizard et fichier local étranger `feedarr-poster-storage-audit.py` préservés.

## Fichiers modifiés

- `docs/matching-metadata-lot4.md`
- `src/Feedarr.Api.Tests/MetadataConsistencyTests.cs`
- `src/Feedarr.Api.Tests/PosterFetchStoreMaterializationTests.cs`
- `src/Feedarr.Api.Tests/PosterFetchTvMazeMetadataRefreshTests.cs`
- `src/Feedarr.Api.Tests/PosterMatchingContractTestRig.cs`
- `src/Feedarr.Api.Tests/TmdbMatchPolicyConsistencyTests.cs`
- `src/Feedarr.Api.Tests/TmdbMetadataBackfillServiceTests.cs`
- `src/Feedarr.Api/Controllers/ReleasesController.cs`
- `src/Feedarr.Api/Data/Migrations/0058_metadata_snapshot_provenance.sql`
- `src/Feedarr.Api/Data/Repositories/ReleaseRepository.HistoryRetention.cs`
- `src/Feedarr.Api/Data/Repositories/ReleaseRepository.MatchingIdentity.cs`
- `src/Feedarr.Api/Data/Repositories/ReleaseRepository.MetadataConsistency.cs`
- `src/Feedarr.Api/Data/Repositories/ReleaseRepository.cs`
- `src/Feedarr.Api/Models/ReleaseForPoster.cs`
- `src/Feedarr.Api/Services/Matching/TmdbMatchPolicy.cs`
- `src/Feedarr.Api/Services/Metadata/RequestTmdbResolverService.cs`
- `src/Feedarr.Api/Services/Posters/PosterFetchService.cs`
- `src/Feedarr.Api/Services/Posters/PosterMatchCacheService.cs`
