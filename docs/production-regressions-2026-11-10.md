# Régressions beta — diagnostic réel et correctifs locaux, 10 novembre 2026

Les correctifs P1 et P2 sont implémentés séparément. Le commit P2 `6219273` est
conservé sans modification. Aucun push ni déploiement n'est effectué. La validation
locale ne constitue pas une confirmation de résolution sur le conteneur de production.

## P1 — cause établie et limites du diagnostic

La production est bien `2.5.18-beta-sha.304572d`, révision OCI
`304572d3c7662b7e10f6d8220273eb111a7fb764`, sur Synology/Portainer. Les rapports
fournis par l'utilisateur confirment un bind mount persistant et accessible de
`/volume1/Docker/Feedarr/data` vers `/app/data`, UID/GID effectifs 10001, de l'espace
libre et aucun redémarrage/OOM du conteneur pendant la période observée.

Les posters représentent **52 293 fichiers et 6 613 367 029 octets**. Le parcours de
leurs métadonnées prend 0,262 s ; ce chiffre ne mesure ni la copie ni les fsync.
Sur `304572d`, chaque préparation copie récursivement tous les posters, avec
`Flush(true)` pour chaque fichier, avant de publier le manifeste. Elle reste exécutée
dans le POST HTTP et reçoit son CancellationToken de la requête.

Les deux échecs, à 11:15:54.757962016 UTC et 11:16:31.462477563 UTC, sont chacun
associés à `OperationCanceledException`, au contrôle d'annulation de `CopyTree`
ligne 331 et à son appel depuis `Publish` ligne 92. La seconde stack comporte aussi
la récursion ligne 337. **Le token HTTP était annulé au contrôle de la copie des
posters, avant toute publication du manifeste.** Le `finally` de `BackupService`
supprime alors le work non publié ; cela explique l'absence de pending, stage et
résultat dans les inventaires suivants, sans perte de volume Docker.

La défaillance de cycle de vie établie est l'attachement d'une préparation volumineuse
à la connexion HTTP qui doit attendre sa fin. Les preuves ne permettent pas de dire
si l'annulation vient du refresh, d'un timeout client/proxy ou d'une autre fermeture
HTTP, ni de mesurer le temps effectivement passé en copie. Le correctif ne suppose
aucun timeout Synology précis et ne prétend pas avoir mesuré un deadlock.

L'utilisateur ne pouvait pas lire le bandeau derrière la modale. `SettingsBackup`
affiche aussi un bandeau orange d'opération en cours avant tout manifeste, distinct
de `RestorePendingBanner`. L'apparition rapide d'un bandeau ne prouve donc pas une
préparation réussie. Les GET de diagnostic retournaient 401 ; ils ne contenaient
aucun état JSON exploitable et n'ont pas été contournés.

### Parcours corrigé

1. Le frontend relit `/api/system/backups/state` avant soumission et demande une
   confirmation explicite avec l'identité courante pour remplacer un pending.
2. Le POST confirmé avec `background=true` vérifie les conflits, un pending existant
   et les stagings incomplets, puis répond rapidement **HTTP 202** avec
   `accepted: true`, `preparationId` et Location `/api/system/backups/state`.
   Cette réponse signifie acceptation, jamais préparation réussie.
3. `BackupService`, enregistré comme service hébergé, possède l'exécution et son
   token. Une fermeture HTTP ne l'annule plus. L'arrêt de l'hôte ou le délai serveur
   de deux heures demandent son annulation. Le travail et ses exceptions sont suivis.
4. Les copies, checksums, fsync, règles de remplacement et publication atomique
   préexistants restent en place. L'identité publiée est celle de la préparation.
   La préparation ne se connecte jamais à SQLite active.
5. Le frontend suit l'identité via les GET de statut ; seule la lecture serveur d'un
   manifeste pending correspondant confirme la préparation. Il ferme alors la modale
   et émet l'événement d'actualisation du bandeau. Un booléen React ou le HTTP 202 ne
   suffit pas. Le manifeste persistant maintient le bandeau après un refresh.
6. Un échec, une perte de suivi ou un timeout produit une erreur explicite et libère
   le loading dans `finally`. Après trois erreurs de polling ou trente minutes de
   suivi, le message précise que le serveur peut encore travailler : aucune nouvelle
   soumission automatique. Chaque requête de suivi a son propre timeout de 15 s.
7. Une ref de soumission bloque les doubles clics avant même un rerender. Boutons,
   croix, Échap et fermeture extérieure sont bloqués pendant l'opération, pour
   restauration et création de sauvegarde. Les erreurs sont lisibles dans les modales.
8. Les erreurs de préparation restent visibles via l'état serveur lors d'une nouvelle
   visite de la page. L'indicateur de copie en cours est distingué d'un pending prêt.

Le contrat historique `/restore?confirm=true` reste synchrone sans le paramètre
`background` : un ancien client ne doit pas interpréter un nouveau HTTP 202 comme une
restauration terminée. Le nouveau frontend utilise explicitement le mode asynchrone.
Il faut donc déployer backend et frontend ensemble et actualiser les anciens onglets.

### Contrôle préalable et conservation

Une préparation concurrente est refusée. Un pending existant exige son ID pour être
remplacé ; son manifeste/stage doit être lisible. Des `building-*`, stages orphelins,
temporaires de pending/journal ou un chemin de contrôle incohérent bloquent une
nouvelle préparation avec une erreur de diagnostic. Ils ne sont pas effacés par ce
contrôle. Les vérifications sous verrou au moment de publier sont conservées.
Un échec du remplacement conserve le pending précédent. Les sauvegardes ZIP et le
format de restauration ne changent pas. Les autres mutations de sauvegarde sont
refusées pendant une préparation active plutôt que lancées en parallèle.

### Fichiers P1

- Backend : `Services/Backup/BackupService.cs`, `PendingRestoreStore.cs`,
  `BackupOperationStateDto.cs`, `Controllers/SystemApiCore.cs`,
  `Controllers/SystemBackupsController.cs`, `Program.cs`.
- Frontend : `pages/settings/hooks/restorePreparation.js`, `useMaintenanceActions.js`,
  `pages/settings/SettingsBackup.jsx`, `ui/Modal.jsx`.
- Tests : `BackupServiceTests.cs`, `SystemControllerBackupRestartRequiredTests.cs`,
  `restorePreparation.test.mjs`, `backupSubmission.test.mjs`,
  `Modal.closeDisabled.test.mjs`, plus les tests de persistance existants.
- Diagnostic fourni : `docker/diagnostics/diagnose-restore.py`, ses tests et
  `docs/restore-diagnostic.md`. Aucun secret/log brut n'est enregistré dans le dépôt.

### Reproduction et validation de P1

Le test HTTP démarre un vrai Kestrel local sur un port éphémère, avec données
entièrement temporaires. La copie est volontairement retenue par une barrière de test.
Il vérifie réception complète du HTTP 202 et de son JSON pendant que la copie est
encore bloquée, annulation du token client, GET d'état `running` sans pending,
puis publication du manifeste correspondant après libération. Le même test vérifie
que l'ancien contrat retourne toujours HTTP 200. Il ne démarre pas le Program de
production et ne modifie ni l'authentification ni les politiques de sécurité.

Les autres tests couvrent une copie lente, doubles soumissions, échec disque, délai
serveur, arrêt de l'hôte, préservation du pending remplacé, staging incomplet conservé,
confirmation uniquement par manifeste, polling temporairement défaillant/perdu,
timeout de suivi, fermeture des modales et sortie du loading pour création/restauration.

### Risques résiduels P1

Le volume de 6,61 Go et les 52 293 écritures durables restent à traiter : le correctif
résout le cycle de vie, pas le coût d'E/S. Le délai serveur est coopératif ; une E/S
bloquée par le NAS peut retarder sa prise en compte, mais le suivi frontend est borné.
L'état détaillé du job est en mémoire ; après arrêt brutal avant publication, aucune
réussite n'est annoncée. La publication durable et le traitement au démarrage restent
ceux du mécanisme existant. Aucun redémarrage de production n'est entrepris ici.

Docker est absent de la machine de développement : pas de reproduction destructive
sur un conteneur local. Les preuves du volume persistant viennent des diagnostics
réels fournis ; la reproduction contrôlée du cycle HTTP emploie Kestrel et un staging
sur disque temporaire. Le comportement du correctif sur le NAS reste à confirmer
après un déploiement décidé par l'utilisateur.

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


## Validation globale et Git

- Tests ciblés Backup/Restore : **50/50 réussis**, dont le POST et les GET via Kestrel réel.
- Suite backend finale : **1 052/1 052 réussis**, comprenant mises à jour, endpoints,
  persistance des manifestes et non-régression SSRF/transports publics.
- Suite frontend finale : **313/313 réussis**, incluant les vrais handlers du hook,
  le suivi de préparation, les protections Modal et le bandeau pending existant.
- Tests du script de diagnostic : **5/5 réussis** (sorties filtrées, erreurs, chemins
  et liens symboliques, positions des exceptions). Aucun contact de production.
- Compilation backend Release : réussie, **0 erreur / 0 avertissement**.
- Vite, ESLint et `git diff --check` : réussis.

Commandes exécutées :

```sh
dotnet test src/Feedarr.Api.Tests --no-restore --filter 'FullyQualifiedName~Backup|FullyQualifiedName~PendingRestore'
dotnet test src/Feedarr.Api.Tests --no-restore
dotnet build src/Feedarr.Api/Feedarr.Api.csproj -c Release --no-restore
npm --prefix src/Feedarr.Web/feedarr-web test
npm --prefix src/Feedarr.Web/feedarr-web run build
npm --prefix src/Feedarr.Web/feedarr-web run lint
python3 -B -m unittest discover -s docker/diagnostics/tests -v
git diff --check
```

Lors de la phase P2, une exécution intermédiaire avait rencontré un timeout d'un test
existant `BadgeSignalCoalescingTests.NotifySeparatedByWindow_TriggersTwoFlushes` ;
ce test temporel n'a pas été modifié. Les suites complètes finales P1 ont réussi.
Risque P2 résiduel : GitHub peut rester inaccessible ou limiter les requêtes ; l'erreur
est désormais visible et le cache conserve le changelog disponible. La récupération
reste limitée aux dix dernières releases, comme auparavant.
Le correctif P2 reste dans `6219273` — `fix(updates): restore release metadata retrieval`.
Le correctif P1 est validé pour le commit distinct
`fix(backup): resolve deferred restore preparation lifecycle`.
Aucun push sur `origin/beta`, conformément à la dernière instruction de l'utilisateur.
Un nouveau déploiement backend et frontend est nécessaire pour appliquer les deux
correctifs ; aucune confirmation de résolution en production n'est encore revendiquée.
