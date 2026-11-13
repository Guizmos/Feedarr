# Wizard beta : Open, visibilité et harmonisation — 13 novembre 2026

Base examinée : `2d0a38d`. Branche `beta`, worktree `/private/tmp/feedarr-wizard-beta`. Aucun changement backend, aucune modification de volumes Docker, aucun commit, push ou déploiement.

## Reproduction et causes racines

Les nouveaux tests montent réellement React 19 en StrictMode, React Router, `AuthGate`, `SetupWizard` et leurs composants dans jsdom, avec le vrai `api/client.js`, le vrai transport de session et les vrais événements DOM. Seules les réponses HTTP sont simulées. Les tests précédents d’interaction remplaçaient notamment les hooks et le transport : ils ne couvraient pas cet enchaînement.

Une exécution ciblée remplaçant le module de session et sa feuille CSS par ceux de `2d0a38d` échoue sur **quatre reproductions** :

- Open effectue **2 GET de session au lieu de 1** après son PUT.
- Avec une lecture de session bloquée, le bouton reste sur **« Enregistrement… »** après le déclenchement du délai attendu par le test.
- `pagehide` démonte le wizard anonyme, alors qu’il devrait rester disponible.
- Au retour d’un onglet authentifié avec une lecture de session bloquée, la visibilité CSS calculée de la page de récupération vaut **`hidden`** au lieu de `visible`.

La copie temporaire utilisée pour cette comparaison a été supprimée ; les sources antérieures restent accessibles dans Git. Journal local de comparaison : `/private/tmp/feedarr-open-baseline-reproduction.log`.

### Chaîne des promesses Open

Avant correction :

1. `Step2Security.save()` passe `saving=true`.
2. `apiPut('/api/settings/security')` attend `createSessionFetch`.
3. Bien que le PUT ait déjà répondu 200, le transport attend `refreshSession()` et masque son éventuelle erreur avec `catch`.
4. Une fois le transport rendu, le formulaire appelle encore `refreshSession()` pour Open.
5. `setSaved(true)` et le `finally` de sauvegarde ne peuvent être atteints avant le règlement de ces attentes.

La vérification utilisait un signal de timeout attaché à `fetch`, mais aucun règlement explicite ne bornait la totalité de la promesse, notamment `response.json()`. Le test utilise volontairement un corps/adaptateur qui ne termine pas et ignore l’abandon : le signal seul ne débloquait pas la chaîne. Le double rafraîchissement ajoutait aussi un second délai et un second point de panne. Cette reproduction établit le chemin bloquant dans le code ; elle ne constitue pas une capture réseau de la panne déployée sur Feedarr-test.

Après correction :

- Pendant un setup incomplet, le transport rend la réponse du PUT sans y imbriquer de GET de session.
- Le formulaire est propriétaire de la suite : **un GET de vérification pour Open**, ou **un POST de connexion pour Smart avec de nouveaux identifiants**. Smart réutilise la réponse de connexion, qui contient déjà l’état vérifié et le CSRF.
- Les opérations d’authentification sont bornées jusqu’à la lecture complète du corps : 15 secondes pour la session, 30 secondes pour login/logout. Le timer et l’écouteur d’abandon sont nettoyés dans `finally` ; un transport qui ignore l’abandon ne retient plus le formulaire indéfiniment.
- Un PUT rejeté ou expiré libère `saving` et expose son erreur. Si les paramètres ont été enregistrés mais que la connexion échoue, une action **« Vérifier l’accès »** reprend l’authentification sans réenvoyer les identifiants au PUT.
- Hors setup incomplet, la vérification automatique du transport demeure active après une modification de sécurité, et son erreur n’est plus avalée silencieusement.

### Propriété de la vérification en cours

L’ancien `pending` était libéré en fonction de la révision de session. Une connexion pouvait changer cette révision pendant un GET déjà engagé : le `finally` du GET ne libérait alors plus `pending`. Des vérifications suivantes pouvaient réutiliser une ancienne promesse au lieu de consulter le serveur.

`pending` possède désormais sa propre identité, sa promesse et son contrôleur. Seule la requête propriétaire libère cette entrée. Une invalidation ou une connexion annule et détache la requête précédente. Une réponse de login arrivant après un verrouillage ne peut plus rouvrir l’état privé. Une réponse de connexion malformée ne peut pas publier un état « ready » incomplet et laisser le routeur en vérification permanente.

### Page blanche au retour d’onglet

`html.auth-locked #root { visibility: hidden }` masquait aussi la page de vérification et le bouton Réessayer. La classe n’était retirée qu’après règlement de la promesse de revalidation. Une promesse bloquée rendait donc toute récupération invisible.

- Le masquage cible maintenant la frontière de contenu privé de `AuthGate`, ainsi que les portails rendus hors de `#root` (modales notamment). Les écrans de vérification et d’erreur demeurent visibles.
- Un wizard connu incomplet **et non authentifié** reste monté lors de `pagehide`/`visibilitychange`. Aucune autorisation API n’est déduite de ce maintien d’affichage.
- Un état authentifié, un état d’application installée ou un état inconnu garde le verrouillage protecteur : invalidation, masquage immédiat du contenu privé, rejet des réponses tardives, puis vérification avant réaffichage.
- `pageshow` et `visibilitychange` partagent une seule vérification en cours, sans se l’annuler mutuellement.
- Une session vérifiée sur un document visible libère aussi un masque résiduel lors d’une action manuelle Réessayer. En cas d’erreur, le masque privé peut rester actif : il ne masque plus la récupération.

### Étape Langue

Le démontage systématique à la perte de visibilité provoquait des remontages et de nouvelles lectures, notamment pour Langue. Les tests vérifient désormais son chargement réel, un HTTP 503, un corps bloqué, l’expiration et la réussite du retry. Une erreur conserve les paramètres inconnus et la sauvegarde désactivée. Il n’existe pas de mesure réseau de Feedarr-test permettant d’attribuer tout le délai observé sur cette étape à une seule cause.

## Sécurité conservée

Aucune route API n’a été ouverte et aucun contrôle backend n’a été modifié. L’échange bootstrap à usage unique, le bootstrap de la cible, SmartAuth, la nécessité HTTPS pour la session web, les cookies HttpOnly, le CSRF, les contrôles d’origine et SSRF restent en place. Les en-têtes de session restent limités aux API de même origine ; les appels Basic explicites restent distincts. Aucune réponse 401/403 n’est convertie en installation vide.

## Harmonisation UI/UX

Réutilisation des composants et classes existants, sans changement de palette, de typographie ou du design global :

- Bootstrap placé dans une `indexer-card`, avec `indexer-row--settings`, label lié au champ, `indexer-actions`, `fieldWrap` et aide `settings-help` référencée par `aria-describedby`.
- Même structure pour les secrets de restauration et le bootstrap de finalisation.
- Reprise de connexion structurée comme le reste du wizard : `setupWizardFrame`, corps défilant, cartes, champs et footer d’actions ; autocomplétion des identifiants conservée.
- Erreurs de Langue et de finalisation rendues avec `InlineNotice`, comme les autres messages du produit. Réduction des doubles espacements des notices dans les groupes déjà munis de `gap`.
- Suppression des styles inline ajoutant des marges à Langue et aux actions de navigation ; alignement repris par les classes existantes.
- `aria-busy`, champs désactivés, retry de chargement de sécurité et interdiction de sauvegarder des paramètres inconnus.
- Largeurs de champs bornées à 100 %, suppression des minima empêchant le rétrécissement, labels/champs empilés sous 600 px et boutons longs autorisés à revenir à la ligne. Ajustements limités au wizard.

### Vérifications visuelles : périmètre exact

Effectués : rendu DOM React réel, associations label/champ/aide, présence et activation des actions, états de chargement/erreur, visibilité CSS calculée du contenu privé, des portails et des écrans de récupération ; revue des règles desktop/mobile et des contraintes de largeur/retour à la ligne.

**Non effectuée : recette visuelle avec captures desktop/mobile dans un navigateur réel.** L’inventaire de contrôle navigateur retourne « browser access host is unavailable » et la tentative d’ouverture native de Safari retourne `cgWindowNotFound`. Un aperçu local avec API fictive a été lancé pour tenter cette vérification, puis arrêté. jsdom ne calcule pas la disposition visuelle et ne permet pas d’attester l’absence de débordement en pixels. Une recette à 1440 px puis 375/320 px reste donc nécessaire, en particulier pour les boutons longs de restauration, les cartes de secrets et les footers.

## Fichiers modifiés

| Fichiers (sous `src/Feedarr.Web/feedarr-web`) | Motif |
| --- | --- |
| `src/auth/session.js` | Propriétaire des vérifications, délais complets, invalidation, réponses tardives et visibilité |
| `src/auth/AuthGate.jsx`, `src/styles/auth.css` | Frontière privée masquée sans cacher la récupération, protection des portails |
| `src/components/setup/Step2Security.jsx` | Sauvegarde/vérification, reprise après échec de connexion, erreurs et carte bootstrap |
| `src/components/setup/Step0Language.jsx` | Notices, labels et état de chargement accessibles |
| `src/components/setup/SetupAccess.jsx` | Formulaire de reprise et actions intégrés au wizard |
| `src/components/setup/StepConfigurationRestore.jsx`, `src/pages/SetupWizard.jsx`, `src/styles/styles.css` | Cohérence des cartes, notices, navigation et règles responsive |
| `src/auth/session.test.mjs` | Délais, identité de requête, concurrence et réponses tardives |
| `src/components/setup/wizardSession.integration.test.mjs` | Montage React/Router réel, transport réel, événements DOM et erreurs HTTP simulées |
| `package.json`, `package-lock.json` | jsdom 26, dépendance de test compatible avec Node 20 utilisé en CI |

## Résultats

- `npm test` : **432 réussis, 0 échec**. Les nouvelles intégrations couvrent Open, Smart, la langue, les erreurs de bootstrap/PUT/login/réseau, les corps bloqués, les délais, la reprise sans second PUT, `pagehide`, BFCache, `visibilitychange`, le masquage privé et le retry après retour d’onglet.
- `npm run lint -- --max-warnings=0` : réussi.
- `npm run build` : réussi.
- `npm run check:categories-domain` : réussi.
- `npm audit --audit-level=high` : aucune vulnérabilité signalée.
- `dotnet test Feedarr.sln -m:1 --no-restore --nologo --filter 'FullyQualifiedName~WebSession|FullyQualifiedName~SmartAuth|FullyQualifiedName~SetupLock|FullyQualifiedName~AntiCsrf'` : **178 réussis, 0 échec**.
- `dotnet build Feedarr.sln -c Release -m:1 --no-restore --nologo` : réussi, **0 avertissement, 0 erreur**.
- `git diff --check` : réussi.

Les journaux de cette intervention sont sous `/private/tmp/feedarr-open-*.log`. Aucun test ne contacte Feedarr-test. Le déploiement et les vérifications derrière son proxy HTTPS restent des opérations distinctes, non exécutées ici.
