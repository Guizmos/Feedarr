# Audit des régressions beta — 10 novembre 2026

## P1 — restauration différée : non reproduite, non déclarée corrigée

Aucun accès à l'instance concernée n'a été fourni pendant cet audit. Aucun manifeste,
staging, conteneur ou fichier SQLite de production n'a été ouvert, modifié ou supprimé.
Aucun conteneur de production n'a été redémarré. L'inventaire local du dépôt ne révèle
pas de `restore-state` ou de fichier `.db` existant ; les tests emploient leurs propres
répertoires temporaires. Docker n'est pas installé sur la machine de validation.

### Parcours vérifié dans le code

1. `pages/settings/hooks/useMaintenanceActions.js` ouvre la modale et envoie le
   POST `/api/system/backups/{name}/restore` sans confirmation pour la prévisualisation.
2. Après confirmation, le hook active `backupRestoreLoading` et attend le POST avec
   `confirm=true`, éventuellement `replacePendingId`, et un timeout de 600 000 ms.
3. `SystemBackupsController` délègue à `SystemApiCore.RestoreBackup`, qui attend
   `BackupService.RestoreBackupAsync`.
4. `BackupExecutionCoordinator.RunExclusiveAsync` attend le sémaphore des opérations.
   La préparation utilise `blockSync: false` : elle n'attend pas le drainage RSS.
5. `BackupService` extrait et valide l'archive, vérifie son checksum et l'intégrité
   SQLite, normalise les credentials dans la copie temporaire, puis appelle `Publish`.
6. `PendingRestoreStore.Publish` copie les posters, répare les références dans la copie,
   flush les fichiers, écrit le manifeste, prend `operation.lock`, vérifie l'identité
   d'une éventuelle restauration précédente, déplace le staging et publie atomiquement
   `restore-state/pending.json` après synchronisation des répertoires.
7. Le coordinateur libère ses états et son sémaphore. Le contrôleur renvoie un objet
   JSON avec `ok: true`, `needsRestart: true`, les compteurs et l'avertissement éventuel.
8. Le frontend émet `restore:changed`, ferme la modale, recharge liste/statut, puis
   remet le loading à false dans `finally`. `api/client.js` borne l'attente du fetch
   ET de son corps JSON avec le signal de timeout.
9. `RestorePendingBanner` lit `/api/system/backups/restore-state` au montage, sur
   l'événement et toutes les 15 secondes. Il conserve le dernier état lors d'une erreur
   réseau. Le backend relit `pending.json` à chaque demande, sans TTL ni cache React.

La préparation retourne et le manifeste reste visible après recréation du store dans
les tests. Un manifeste valide présent dans le même DataDir ne peut pas disparaître
par une simple actualisation du navigateur dans ce parcours. Les observations de
production ne suffisent donc pas à attribuer une cause exacte : il faut comparer le
SHA effectivement déployé, les réponses réseau, les logs, les volumes et le contenu de
`restore-state` sur l'instance concernée. Les différences de volume, plusieurs instances,
une ancienne version frontend/backend ou une erreur d'E/S restent des hypothèses,
pas des diagnostics établis.

### Défauts de protection constatés, sans correctif supposé

- Le loading React seul ne protège pas deux appels synchrones avant le rerender.
- Les callbacks refusent la fermeture pendant le loading, mais la croix du composant
  `Modal` n'est pas visuellement désactivée.
- Le hook accepte une réponse POST sans vérifier strictement `ok` ni relire le manifeste
  pour confirmer l'identité de la préparation.
- Le contrôle d'un pending existant intervient dans `Publish`, après extraction/copie ;
  l'inventaire d'un staging incomplet n'est pas exposé avant une nouvelle préparation.

Ces points ne démontrent pas la cause du blocage et du bandeau éphémère. Aucun correctif
P1 ni commit intitulé « resolve deferred restore preparation lifecycle » n'est créé à
ce stade. Ne pas relancer une préparation en production avant l'inventaire du pending,
des `building-*`, des `stages`, de `operation.json` et du volume monté. Ne rien effacer.
Les protections supplémentaires demandées restent à mettre en œuvre avec le correctif
P1, une fois son diagnostic établi.

## P2 — métadonnées de release : cause reproduite et correction ciblée

### Cause et reproduction

Le workflow `.github/workflows/docker-release-beta.yml` produit une version telle que
`2.5.18-beta-sha.1234567`. `ReleaseInfoService` détecte alors le track prerelease et
`FilterReleasesForTrack` supprime toutes les releases stables. Le 10 novembre 2026,
l'API publique https://api.github.com/repos/Guizmos/Feedarr/releases?per_page=10 a renvoyé
dix releases stables, dont `v2.5.17`, publiée le 27 octobre 2026 à 18:12:38 UTC.
La récupération peut donc réussir tout en produisant `latestRelease: null` et un
changelog vide pour la beta. Le test `BetaWithoutPrereleases...` reproduit ce cas.

Un second défaut masquait les incidents : les exceptions réseau, timeouts et réponses
invalides étaient convertis en liste vide avec HTTP 200 sans indicateur d'erreur. Les
404 étaient explicitement traitées comme une absence de release. Le frontend pouvait
ensuite conserver ce résultat vide six heures et afficher « Inconnu ».

L'audit n'a pas établi de blocage SSRF responsable de cette régression. Le client nommé
`github-updates` utilisait même le transport .NET par défaut, contrairement aux autres
providers publics durcis.

### Correction

- Si aucune prerelease n'existe pour une beta, conserver les releases stables pour les
  métadonnées et le changelog. La comparaison de mise à jour reste liée au track :
  une stable n'est pas proposée comme mise à jour beta.
- Ajouter `error` au résultat et au DTO ; distinguer liste `[]`, réponse invalide,
  erreur réseau, timeout, 403/429 et autre échec HTTP, dont 404.
- Propager l'annulation du demandeur. Conserver le changelog en cache lors d'un échec,
  avec son erreur ; limiter les nouvelles tentatives via le délai du circuit existant.
- Préserver l'erreur dans le store de badges et afficher « Erreur de récupération ».
  Une vraie liste vide affiche « Aucune release disponible ».
- Invalider l'ancien cache navigateur vide sans champ `error` ; réessayer les résultats
  en erreur après deux minutes au lieu de les masquer six heures.
- Appliquer `UsePublicProviderTransport` à `github-updates` : validation DNS avant
  requête et connexion sur IP validée, politique publique stricte, redirections
  contrôlées sans transmission du token vers un autre hôte, sans proxy ni cookies,
  validation TLS standard. Aucune protection SSRF n'est désactivée.

### Fichiers concernés

Backend : `Services/Updates/ReleaseInfoService.cs`, `Services/Updates/UpdateDtoMapper.cs`,
`Dtos/Updates/UpdateCheckDto.cs`, `Program.cs`.
Frontend : `badges/useBadgeStore.js`, `badges/badgeRegistry.js`, `badges/badgeStorage.js`,
`pages/system/SystemUpdates.jsx`.
Tests : `ReleaseInfoServiceTests.cs`, `SystemUpdates.test.mjs`, `updateCache.test.mjs`,
`badgeEngine.contract.test.mjs`.

### Limites et déploiement

La correction P2 est validée localement ; le conteneur de production n'a pas été testé.
Le champ JSON `error` est additif. Les données précédemment en cache restent disponibles
avec avertissement lors d'une panne GitHub. La fenêtre de récupération reste celle des
dix dernières releases, comme auparavant. Un nouveau déploiement backend ET frontend
est nécessaire pour appliquer P2. Le P1 demeure ouvert : ne pas annoncer que les deux
régressions sont corrigées et ne pas pousser l'ensemble sur `origin/beta`.

## Validation exécutée

- Backup/Restore ciblés : 42/42 réussis, répertoires SQLite temporaires uniquement.
- Releases/DTO/endpoints/transports publics/SSRF ciblés : 111/111 réussis avant les deux
  cas supplémentaires de sélection de track ; ces deux cas passent dans la suite finale.
- Suite backend finale : 1 044/1 044 réussis (`dotnet test src/Feedarr.Api.Tests --no-restore`).
- Une exécution intermédiaire a échoué sur un timeout de
  `BadgeSignalCoalescingTests.NotifySeparatedByWindow_TriggersTwoFlushes`, test existant
  sans modification. Une première suite complète puis la suite finale ont réussi.
  Risque résiduel : ce test temporel peut être instable sous charge.
- Suite frontend finale : 300/300 réussis (`npm test`).
- `dotnet build src/Feedarr.Api/Feedarr.Api.csproj -c Release --no-restore` : réussi,
  zéro erreur, zéro avertissement.
- `npm run build` (Vite), `npm run lint` (ESLint) et `git diff --check` : réussis.
- Les tests d'endpoint sont des appels au contrôleur avec vérification du DTO renvoyé ;
  ce n'est pas une validation HTTP à travers le reverse proxy de production.
- Reproduction Docker/volume persistant : non exécutée, Docker absent. La persistance
  sur disque après recréation de store est couverte par les tests locaux.

## Git

Seul le correctif P2 remplit les conditions de correction comprise et validée pour un
commit `fix(updates): restore release metadata retrieval`. Pas de commit P1 supposé.
Aucun push sur `origin/beta`, le diagnostic et la correction P1 restant incomplets.
