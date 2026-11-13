# Audit et correction du wizard beta — 13 novembre 2026

Périmètre : branche `beta`, base de départ `709039e`. Travail dans un worktree isolé ; aucun accès à Feedarr-test, aucune modification de ses données, aucun commit, push ou publication Docker.

## Causes démontrées et correctifs

1. **La sauvegarde de sécurité démontait le wizard.** `createSessionFetch` appelait `lockSession()` après `PUT /api/settings/security`, puis lançait une vérification asynchrone. `AuthGate` remplaçait alors son `Outlet` par son écran de vérification. `Step2Security` ne créait aucune session web : une fois les identifiants enregistrés, les appels suivants à distance recevaient 401. La vérification reste désormais en place pendant un setup incomplet ; le formulaire ouvre une session HTTPS avec les identifiants nouvellement saisis. Hors setup, le verrouillage de la partie privée reste actif.
2. **Une installation partiellement configurée n’avait pas de connexion dans le wizard.** `sessionRoute` donne volontairement priorité au setup incomplet, même si une authentification est nécessaire. L’ancien wizard ne proposait pas de login. `SetupAccess` propose désormais une connexion aux identifiants de la cible pour le parcours d’installation, y compris après une expiration. Le retour au choix reste disponible avant restauration appliquée. Le parcours de restauration conserve son autorisation bootstrap indépendante.
3. **Le formulaire redemandait une sauvegarde à chaque remontage.** Son indicateur `saved` repartait à `false`, même avec une sécurité déjà configurée. Une nouvelle sauvegarde révoquait encore la session. Le chargement d’une sécurité existante autorise désormais « Suivant » ; modifier un champ réactive l’obligation d’enregistrer.
4. **Le bootstrap n’était pas intégré à la création des identifiants.** Le formulaire échange maintenant le bootstrap cible contre un token à usage unique, utilisé uniquement pour le PUT de sécurité. Le middleware refuse explicitement ce PUT à distance sans token valide quand l’authentification est requise mais pas encore configurée. Il conserve la découverte en lecture et l’accès local autorisé. Le bootstrap n’est stocké ni dans localStorage ni dans sessionStorage.
5. **La vérification après restauration perdait l’autorisation de la cible.** `checkRestart` et `finish` lisaient `/api/setup/state` avec `apiGet`, sans bootstrap. Après restauration d’une sécurité Smart exposée ou Strict, ce GET recevait 401. Les deux appels transmettent désormais le bootstrap cible avec `configurationRequest`. Ils ne confondent pas ce secret avec la phrase de déchiffrement de l’archive.
6. **Le verrou de confirmation existait seulement dans l’interface.** Le service acceptait `CancelAsync` après confirmation. Il renvoie maintenant 409 sans supprimer le staging. Un middleware placé après SmartAuth et CSRF bloque aussi les mutations API pendant l’attente de redémarrage, tout en conservant lectures et connexion/déconnexion sous leurs protections habituelles. Le contrôle d’empreinte transactionnel reste la protection contre une modification concurrente déjà engagée au moment de la confirmation.
7. **Des erreurs pouvaient fabriquer un état vide.** L’échec de lecture de langue fabriquait des paramètres par défaut, les erreurs de chargement de sources/applications effaçaient leurs listes et une erreur d’autorisation pouvait déclencher le repli sur les fournisseurs locaux. Les valeurs chargées sont conservées ; une langue inconnue ne peut pas être sauvegardée ; les refus d’autorisation interrompent les replis concernés. Le résumé refuse la finalisation si son chargement échoue et propose de réessayer.
8. **Une réponse perdue n’avait pas d’action de réconciliation explicite.** La restauration propose désormais « Revérifier l’état de restauration » : elle relit le staging persistant, sans réenvoyer le fichier ni assimiler un refus à une absence de plan.

Les routes de langue sont explicitement autorisées avant configuration de la sécurité : une installation vierge n’est donc pas bloquée systématiquement à cette étape. Un 401 sur ces routes avec des identifiants déjà configurés est traité par la connexion à la cible.

## Parcours d’installation et appels API

- `GET /api/auth/session` : 200 avec `setupCompleted`, `authRequired`, `authConfigured`, `authenticated` et, si connecté, le CSRF. Un état inconnu ou une erreur ne signifie jamais « installation vierge ».
- `setupCompleted=false` impose `/setup`. `SetupWizard` lit `GET /api/system/onboarding` et `GET /api/setup/state`, initialise le choix et l’étape locale, puis réconcilie les étapes manquantes d’une restauration appliquée. Les valeurs locales ne constituent pas une preuve d’autorisation.
- Nouvelle installation : langue (`GET/PUT /api/settings/ui`), introduction, sécurité. Si la cible a déjà des identifiants et exige une authentification, la connexion précède les lectures protégées du parcours.
- Première création distante protégée : `POST /api/setup/bootstrap-token` avec `X-Bootstrap-Secret` cible → 200, token éphémère ; absence/erreur → 403. `PUT /api/settings/security` avec `X-Feedarr-Bootstrap-Token` → 200 ; token invalide ou réutilisé → 401. Les erreurs de validation restent 400. Une nouvelle tentative demande un nouveau token.
- `POST /api/auth/login` en HTTPS, même origine → 200 et cookie HttpOnly + CSRF ; identifiants invalides → 401 ; HTTP → 400 `https_required` ; origine invalide → 403 ; limitation → 429. Le formulaire peut ensuite reprendre sans réenregistrer la sécurité.
- Métadonnées : `/api/providers/external` et ses opérations d’instances. RSS : tests Jackett/Prowlarr, `PUT /api/setup/indexer-providers/{type}`, découverte `/api/providers/{id}/indexers`. Sources : `/api/sources`, CAPS et `/api/categories`. Applications : `/api/apps`, configuration et tests. Ces appels restent sous le verrou de setup, SmartAuth, CSRF et les contrôles SSRF existants.
- Résumé : lectures des fournisseurs, sources, catégories, applications. `POST /api/system/onboarding/complete` → 200, puis nettoyage de la navigation locale et `refreshSession()`. `AuthGate` seul choisit ensuite `/library` ou `/login` suivant la réponse réelle du serveur.
- Les navigations précédente/stepper/retour sont bloquées pendant les enregistrements langue/sécurité et la finalisation. Avant ces opérations, le choix des deux parcours reste réversible.

## Parcours de restauration et garanties

- Fichier ZIP non vide, limite 64 Mio ; le frontend rejette une sélection manifestement invalide avant envoi. Phrase de sauvegarde V2 et bootstrap de la cible sont deux champs distincts. Une ancienne archive compatible non chiffrée accepte une phrase vide.
- `GET /api/setup/configuration-restore/state` réconcilie le staging lors d’une reprise. Une réponse 401/403 reste une erreur visible ; elle ne constitue pas un plan vide.
- `POST .../prepare` multipart avec archive et phrase, bootstrap cible en header : 200 et aperçu ; 422 pour archive invalide/phrase erronée ; 413 pour dépassement ; 403 pour autorisation manquante ; 409 en cas de conflit. La phrase est effacée du formulaire lors de l’envoi. La préparation ne modifie aucune configuration active.
- Le plan contient uniquement les paramètres et connexions autorisés par `ConfigurationSchema` et `ConfigurationSnapshotReader`. Le lecteur legacy convertit la configuration en mémoire : il ne remplace jamais la base active. Releases, entités, posters et caches ne sont pas importés. Les secrets importables sont reprotégés avec les clés de la cible.
- Après consentement au remplacement et, si nécessaire, au changement de sécurité, `POST .../{id}/confirm` → 200 avec `confirmed=true`. Avant confirmation, `DELETE .../{id}` → 204 permet de revenir au choix. Après confirmation, l’annulation et les mutations API sont refusées avec 409 et une instruction de redémarrage.
- Le staging chiffré/authentifié est conservé dans le répertoire de données (`configuration-restore/plan.bin`), avec écriture durable. Redémarrer le même conteneur avec le même volume et les mêmes clés applique le plan avant le démarrage des workers/HTTP.
- L’application utilise une transaction SQLite et une empreinte de configuration. Un échec avant commit annule les modifications et garde le staging. Un crash après commit est réconcilié grâce au marqueur d’opération, sans double application. Les références historiques et fichiers existants sont conservés.
- Vérification après redémarrage : `GET /api/setup/state` avec bootstrap cible. Si terminé : étape Finaliser puis vérification de session/redirection. Sinon : lecture du staging et reprise des étapes manquantes ; après connexion si nécessaire, `POST /api/setup/configuration-restore/complete` valide la configuration avant de terminer.
- La resynchronisation est persistante dans `configuration_restore_operations`. Elle repart automatiquement pour une cible vierge complète, reste en attente pour une configuration incomplète ou une bibliothèque existante, et conserve progression/retry par source après interruption. La rétention importée plus stricte requiert sa confirmation séparée.

## Fichiers

- `src/Feedarr.Api/Services/Security/BasicAuthMiddleware.cs` : autorisation du premier PUT de sécurité distant.
- `src/Feedarr.Api/Services/Backup/Configuration/ConfigurationRestoreService.cs` : interdiction d’annuler un plan confirmé.
- `src/Feedarr.Api/Services/Backup/Configuration/ConfigurationRestoreLockMiddleware.cs`, `src/Feedarr.Api/Program.cs` : verrou serveur des mutations après confirmation, après les contrôles d’authentification et CSRF.
- `src/Feedarr.Web/feedarr-web/src/api/client.js` : headers explicites pour l’échange bootstrap ciblé.
- `src/Feedarr.Web/feedarr-web/src/auth/session.js` : réconciliation en place pendant le setup.
- `src/Feedarr.Web/feedarr-web/src/components/setup/SetupAccess.jsx`, `Step2Security.jsx`, `src/Feedarr.Web/feedarr-web/src/pages/SetupWizard.jsx` : connexion, bootstrap, reprise sans boucle et verrouillage de navigation.
- `Step0Language.jsx`, `Step31JackettIndexers.jsx`, `Step4ArrApps.jsx`, `Step5Summary.jsx` dans le même dossier setup : conservation des états inconnus/chargés et récupération des erreurs.
- `useSetupRestore.js`, `StepConfigurationRestore.jsx`, `../backup/configurationBackupApi.js` : autorisation post-redémarrage, réconciliation et messages.
- Régressions : `SmartAuthMiddlewareTests.cs`, `ConfigurationRestoreServiceTests.cs`, `ConfigurationBackupsControllerTests.cs`, `auth/session.test.mjs`, `components/backup/configurationBackup.interactions.test.mjs`.

## Vérifications sur Feedarr-test après déploiement autorisé

Aucun déploiement n’est effectué par cette intervention. Vérifier sur une cible jetable utilisant la même configuration de proxy :

1. HTTPS effectif reconnu par ASP.NET via les seuls proxies approuvés, origine identique et cookies Secure transmis. Aucun assouplissement de ces protections n’est nécessaire.
2. Bootstrap configuré sur la cible, volume de données et clés Data Protection persistants. Le bootstrap de l’ancienne instance ne remplace pas celui de la cible.
3. Installation dans un navigateur neuf et reprise dans un navigateur avec un ancien `feedarr:setupStep` ; création des identifiants, session, paramètres, finalisation et arrivée dans l’application.
4. Import d’une sauvegarde V2 puis d’une archive legacy représentatives ; contrôle des connexions et étapes de secrets manquants ; absence d’import de bibliothèque/posters.
5. Perte réseau pendant préparation/confirmation : utiliser « Revérifier l’état » avant de réenvoyer. Après confirmation, redémarrer le conteneur avec le volume intact. Vérifier progression de resynchronisation et éventuelles confirmations de rétention.
6. Si le démarrage refuse le plan (empreinte modifiée, clés perdues, destination interdite), conserver le volume et le staging, examiner les logs et rétablir la condition signalée. Ne pas supprimer le staging ou remplacer la base réelle pour contourner le contrôle. L’échec avant commit préserve la configuration active.

Les tests automatisés utilisent des fixtures temporaires et un ordonnanceur de hooks pour les interactions React ; ils ne remplacent pas une recette navigateur derrière le proxy réel.

## Tests et contrôles exécutés

| Contrôle | Résultat |
| --- | --- |
| `dotnet test Feedarr.sln -m:1 --no-restore --nologo` | 2 013 réussis, 0 échec, 0 ignoré ; toute la suite après ajout du verrou serveur |
| Dernière vérification `--filter 'FullyQualifiedName~SmartAuthMiddlewareTests'` | 94 réussis, 0 échec, incluant les variantes slash final et casse ajoutées après la suite complète |
| `npm test` | 411 réussis, 0 échec |
| `dotnet build Feedarr.sln -c Release -m:1 --no-restore --nologo` | Réussi, 0 avertissement, 0 erreur |
| `npm run build` | Réussi |
| `npm run lint -- --max-warnings=0` | Réussi |
| `npm run check:categories-domain` | Réussi |
| `npm audit --audit-level=high` | 0 vulnérabilité signalée |
| `dotnet list Feedarr.sln package --vulnerable --include-transitive` | Aucun package vulnérable signalé, dépendances transitives incluses |
| `git diff --check` | Réussi |

La première exécution backend parallèle a saturé temporairement l’espace disponible (660 échecs, notamment `No space left on device`). Après libération automatique des fixtures, les exécutions séquentielles passent. Pour ces exécutions locales, `xunit.runner.json` a été créé uniquement dans le répertoire de sortie Debug ignoré par Git, avec `parallelizeTestCollections=false` et `maxParallelThreads=1`. Aucun changement de configuration de test n’est ajouté aux sources.

Couverture de régression :

- Installation distante : échange bootstrap/token, sauvegarde des identifiants, ouverture de session, finalisation et nettoyage de navigation ; reprise de sécurité déjà configurée sans boucle.
- Refus bootstrap absent/invalide/réutilisé, protections de session et CSRF, origin et SSRF : nouvelles assertions et suites existantes `SmartAuthMiddlewareTests`, `WebSessionTests`, `SetupLockMiddlewareTests`, tests de politique sortante.
- Sauvegarde valide V2/legacy, mauvais mot de passe, archive invalide, checksums et tailles : `ConfigurationArchiveServiceTests` et `ConfigurationBackupsControllerTests`.
- Préparation sans mutation active, refus d’annulation confirmée, verrou des mutations, rollback aux points d’injection, reprise après crash avant/après commit, progression de sync persistante : `ConfigurationRestoreServiceTests`.
- Bibliothèque existante inchangée, aucune importation de releases, lecture legacy sans bibliothèque, posters conservés : tests existants de snapshot et de conservation byte pour byte.
- Retour/choix, réponses perdues, reprise confirmée/appliquée, autorisation post-redémarrage, 401/403 conservés comme erreurs, état vide accepté seulement après réponse réussie : tests d’interaction du wizard et du transport de session.
