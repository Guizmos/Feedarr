# P0 — Open et environnement DOM de CI

Intervention sur `beta` uniquement, dans `/private/tmp/feedarr-wizard-beta`, à partir de `36d6688`. Aucun commit, push, déploiement, accès à Feedarr-test ou manipulation de volume Docker.

## 1. CI : reproduction et correction

La suite initiale a été exécutée avec **Node v20.20.2**, version majeure configurée par `.github/workflows/ci.yml` : **440 tests, 418 réussis, 22 échecs**. Les erreurs `ReferenceError: navigator is not defined` sont reproduites localement avec ce runtime.

Les fixtures installaient `window` et `document`, mais importaient `react-dom/client` sans installer `navigator`. Node 26 fournit ce global ; Node 20 ne le fournit pas. L’environnement local masquait donc le défaut. Restaurer les globals par affectation ajoutait aussi des propriétés qui étaient auparavant absentes et ne préservait pas les accesseurs en lecture seule de Node récent.

Correction exclusivement dans les tests pour ce problème :

- `src/test/domEnvironment.mjs` installe les globals DOM nécessaires **avant** l’import de React DOM, notamment `navigator`, `getComputedStyle`, événements, stockage et fonctions d’animation.
- Chaque propriété est sauvegardée et restaurée par son descripteur. Une propriété précédemment absente est supprimée ; les accesseurs sont conservés sans être invoqués. Le nettoyage est idempotent et s’exécute aussi après une initialisation échouée.
- `ResizeObserver` est inerte dans ce seul environnement de test : jsdom ne calcule pas la disposition. Aucun shim n’est ajouté à la production.
- Les requêtes HTTP réelles sont terminées avant le démontage et la restauration des globals, pour éviter les mises à jour React tardives après disparition de `window`.
- Les fixtures communes remplacent les initialisations divergentes des tests wizard et ErrorBoundary. Des tests couvrent explicitement navigator absent, navigator en lecture seule et restauration des fonctions HTTP/timers.

Validation après `npm ci`, sous Node 20 : **449 tests, zéro échec**. Même résultat sous le Node 26 local : **449/449**. Le runtime Node 20 a été exécuté sur l’hôte macOS ; aucun job GitHub Actions Linux n’a été lancé, conformément à l’interdiction de push. Le workflow garde Node 20 et exécutera également les nouveaux parcours HTTP sur Ubuntu lors d’une exécution autorisée de la CI.

## 2. Open : promesse bloquante démontrée

Le symptôme confirmé est le bouton **« Enregistrement… »**. Un nouveau test monte le vrai React/Router, le vrai wizard et le transport réel, puis contacte un vrai backend ASP.NET avec SQLite temporaire. Il retient une réponse de session démarrée avant la sauvegarde pour reproduire une réponse retardée de réseau/proxy.

Avec les modules de production d’avant correction :

1. Découverte de session : GET `/api/auth/session` → 200, Smart, setup incomplet.
2. Un autre GET de session est engagé **avant l’écriture** ; sa réponse est retardée.
3. POST `/api/setup/bootstrap-token` → 200.
4. PUT `/api/settings/security`, mode Open → **200** et état réellement persisté.
5. Le formulaire appelle `refreshSession()`. La condition `if (pending) return pending.promise` lui fait attendre la promesse démarrée avant le PUT.
6. Aucun nouveau GET de vérification après écriture n’est lancé. `setSaved(true)` et le `finally` qui libère `saving` restent suspendus à cette ancienne promesse ; le test voit toujours **« Enregistrement… »**, malgré le PUT 200.

Ce test échoue sur les anciens modules et réussit sur les nouveaux **avant de libérer l’ancienne réponse**. Le journal de reproduction contient le PUT 200 et l’absence de relecture après écriture : `/private/tmp/feedarr-open-spinner-before.log`. Il s’agit d’une reproduction contrôlée du chemin bloquant, pas d’une capture réseau de Feedarr-test.

Correction :

- `refreshSession({ force: true })` détache et annule une vérification antérieure. Seule la requête encore propriétaire de `pending` peut publier son succès ou son erreur. Une réponse obsolète ne peut pas remplacer l’état frais.
- Après PUT réussi, Open effectue une vérification fraîche de session, puis GET `/api/settings/security`. Le mode et l’absence de credentials/authentification sont contrôlés sur l’état **relu**, avant d’activer Suivant.
- Smart et Strict ouvrent leur session avec les identifiants saisis puis relisent les paramètres persistés. Ils ne demandent pas une session Open ni une seconde autorisation bootstrap après succès.
- Les délais existants restent appliqués à la requête et au corps : 30 secondes pour l’API, 15 secondes pour la session. Le formulaire libère `saving` dans `finally`.
- Le bouton distingue maintenant autorisation bootstrap, écriture, vérification d’accès et relecture. Une erreur présente le **statut HTTP et le détail réel** du serveur. Si une écriture est incertaine, l’action propose de vérifier l’état avant de réécrire.
- Les échecs de session restent aussi visibles dans `AuthGate`, avec leur statut et leur cause, plutôt que seulement un message générique de réseau.

## 3. Autre blocage Open démontré

Un mot de passe prérempli/saisi en Smart, avec confirmation vide, restait validé après sélection de Open. Les champs étaient cachés, mais `pwdInvalid` provoquait un retour avant tout PUT. Le nouveau test échoue avant correction avec **0 PUT au lieu de 1** ; journal `/private/tmp/feedarr-open-hidden-before.log`.

Open ignore désormais ces champs dans la validation **et dans le payload** : aucun username, password ou passwordConfirmation n’est transmis. La relecture remplace les champs locaux par les valeurs persistées, y compris les chaînes vides. Le statut `authRequired` reflète la réponse persistée et n’est plus maintenu artificiellement à vrai par l’ancien prop `required`.

Ce défaut est distinct du spinner : la validation empêchait le début de l’enregistrement, tandis que la promesse de session ancienne retenait un enregistrement déjà réussi.

## 4. Audit HTTP et sécurité

Les parcours sont exécutés sans Docker, sur des processus ASP.NET temporaires liés à `127.0.0.1`, avec un répertoire SQLite/clé neuf par cas. Le cas distant transmet les headers d’un proxy approuvé avec schéma public HTTPS, ce qui exerce les contrôles HTTPS/origine de l’application dans le même mode qu’une terminaison TLS par proxy. Aucune validation TLS de production n’est désactivée.

| Opération | Réponse constatée |
| --- | --- |
| Session distante neuve | 200, Smart, `authRequired=true`, credentials absents, setup incomplet |
| Bootstrap distant absent ou incorrect | 403 `bootstrap_secret_required` |
| Bootstrap cible correct | 200, token à usage unique |
| PUT initial Open avec token valide | 200 ; aucune identité ni mot de passe requis dans le body |
| Relecture de session après Open | 200, Open, `authRequired=false`, `authConfigured=false` |
| Relecture de sécurité après Open | 200, Open, username vide, `hasPassword=false` |
| PUT initial Open en accès local autorisé | 200 sans demande de bootstrap |
| Smart / Strict, credentials valides | PUT 200, login 200 et cookie/CSRF, relecture 200 |
| PUT sans authentification sur instance configurée | 401 |
| PUT avec cookie mais sans CSRF | 403 |
| Passage d’une instance configurée Smart/Strict vers Open sans confirmation de downgrade | 400, ProblemDetails `title=downgrade_confirmation_required` ; configuration conservée |

`BasicAuthMiddleware.cs`, `SettingsController.cs` et `SetupAccess.jsx` ont été examinés ensemble avec le formulaire et le transport. Le middleware autorise le premier PUT distant via le token émis après preuve bootstrap. Le contrôleur distingue l’installation initiale et le downgrade d’une configuration disposant déjà d’identifiants. **Aucune modification backend de production et aucun élargissement de route/autorisation.** Le wizard n’envoie pas `allowDowngradeToOpen` pour contourner la protection.

## 5. Tests de parcours et CI

Commande nouvelle : `npm run test:wizard-http`, après compilation Release du backend. Le workflow CI l’exécute après la suite frontend et après les tests/backend build Release déjà existants.

Les sept tests HTTP couvrent :

1. Open local sans bootstrap, credentials masqués ignorés, Métadonnées, rechargement et reprise.
2. Open derrière proxy HTTPS approuvé, un seul bootstrap valide, relecture, Métadonnées et reprise sans réauthentification.
3. Smart, session/CSRF, Métadonnées et refus de downgrade non autorisé.
4. Strict, mêmes contrôles.
5. Bootstrap invalide : vrai 403 visible, récupération puis Métadonnées sans boucle de login.
6. GET de session antérieur retardé : PUT 200 puis sauvegarde débloquée sans attendre la réponse obsolète.
7. Changement d’onglet pendant la réponse du PUT : navigation bloquée pendant l’opération, puis reprise sans seconde demande bootstrap ni login.

La suite frontend couvre aussi les PUT et vérifications de session 400/401/403 avec cause et récupération, les délais dépassés, la navigation, les globals, ainsi que les régressions de visibilité et d’espacement précédentes.

## 6. Fichiers et résultats

Sous `src/Feedarr.Web/feedarr-web` :

- Production : `src/components/setup/Step2Security.jsx`, `src/pages/SetupWizard.jsx`, `src/auth/session.js`, `src/auth/AuthGate.jsx`.
- Tests et helpers : `src/test/domEnvironment.mjs`, `src/test/domEnvironment.test.mjs`, `src/test/wizardDomFixture.mjs`, `src/components/setup/wizardSession.integration.test.mjs`, `src/ui/__tests__/ErrorBoundary.layout.test.mjs`, `src/components/backup/configurationBackup.interactions.test.mjs`.
- Parcours HTTP : `scripts/test-wizard-http.mjs`, script npm dans `package.json`, nouvelle étape de `.github/workflows/ci.yml`.

| Vérification | Résultat |
| --- | --- |
| Suite frontend avant correction, Node 20.20.2 | 22 échecs reproduits |
| Suite frontend après `npm ci`, Node 20.20.2 | **449/449 réussis** |
| Suite frontend Node 26 | **449/449 réussis** |
| Parcours React + HTTP + ASP.NET + SQLite réels, Node 20 | **7/7 réussis** |
| Suite backend complète .NET 8 | **2015/2015 réussis** |
| Build frontend sous Node 20 | Réussi |
| Build .NET Release | Réussi, 0 avertissement / 0 erreur |
| Lint frontend strict | Réussi |
| Contrôle du domaine catégories | Réussi |
| Audit npm et NuGet, transitives incluses | Aucune vulnérabilité signalée |
| `git diff --check` | Réussi |

Les journaux finaux sont sous `/private/tmp/feedarr-p0-*.log`. Les copies temporaires des anciens modules utilisées pour démontrer l’échec ont été supprimées du worktree. Les helpers/identifiants fictifs des tests ne sont pas présents dans le bundle frontend livré.

Aucune action distante sur GitHub n’a été déclenchée. Après déploiement autorisé par l’utilisateur, vérifier le même enchaînement sur Feedarr-test ; les phases affichées et les statuts conservés permettent désormais d’identifier un éventuel refus spécifique au proxy ou au bootstrap de cette instance.
