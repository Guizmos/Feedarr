# Branches et publications Feedarr

Migration du 28 novembre 2026 : cycle quotidien `dev → beta → main`.

## Références et sauvegarde

| Branche | Référence de migration |
| --- | --- |
| main, engine_v1 | `944c66bb231dded6ad4af6e88c7bcbfd1ce180b4` |
| beta | `15ef6a70bf46aa8f8d3b9d8702f836190a4b4a26` (inchangée) |
| dev | Créée depuis beta ; reçoit ensuite uniquement les changements CI/CD et cette documentation |
| legacy-auth | `944c66bb231dded6ad4af6e88c7bcbfd1ce180b4` (conservée) |
| backup/main-before-engine-v1-20261128 | `0705a3d9dcd087d7ab42120c8aba4469a36b8d85` |

La référence V1 est acceptée explicitement par le propriétaire comme référence
présumée de production. L'historique shell montre un build multiarchitecture avec
`FEEDARR_VERSION=2.5.20-legacy-auth` après le commit `944c66b`, mais aucune preuve
ne relie ce contexte Git au digest NAS
`sha256:46449b6f72d5b8cb5d977a10f5cfd1bc8de6345d6163b5ac08ce8312c47c1d0e`.
L'archive représente cette référence Git acceptée, pas une provenance Docker certifiée.

Une sauvegarde Git bundle a aussi été vérifiée à
`/private/tmp/feedarr-migration-20261128/before.bundle` (fichier temporaire local).
La sauvegarde durable est la branche distante verrouillée.

## Fonctionnement des workflows

- `ci.yml` : push/PR vers dev, beta et main ; réutilisable pour tester exactement
  le commit d'un tag avant sa publication stable.
- `publish-beta.yml` : push beta uniquement, tags Docker `beta` et `beta-<sha>`.
  Aucun commit de version ; `FEEDARR_VERSION` est injectée au build.
- `publish-stable.yml` : événement de tag `v*`, puis validation stricte `vX.Y.Z`
  sans zéro initial, vérification d'accessibilité depuis main, tests réussis,
  nouvelle vérification avant publication. Tags Docker `latest`, `vX.Y.Z`, `X.Y.Z`.
- Aucune création de tag ou release GitHub automatique. Aucun publish sur push
  dev, main ou engine_v1. Permissions GitHub des nouveaux workflows : lecture seule.
- Les futures images déclarent les labels OCI source, revision, version et created.

Les numéros stables doivent dépasser tous les tags stables existants du dépôt,
même ceux qui ne sont plus ancêtres de main. La prochaine stable dépasse donc
`v2.5.20`. La version beta est le prochain patch stable global, suivi de
`-beta.<run_id>.sha.<sha>` ; elle ne régresse pas vers 2.5.17 après le déplacement
de main. Une réexécution du même run réutilise son numéro. Les publications sont
sérialisées ; un ancien push beta qui n'est plus la tête de branche est refusé.

Les anciens workflows `docker-publish.yml`, `docker-release-beta.yml`,
`docker-release.yml`, `release-please.yml` restent désactivés au niveau du dépôt.
Ils existent encore dans les références historiques, main et beta : ne pas les
réactiver. Sur dev, les trois derniers et le script d'autorelease sont supprimés.
Les nouveaux workflows ont des chemins distincts pour ne pas réactiver les
anciennes définitions. Leur arrivée sur dev ne publie rien.

La publication beta reste suspendue jusqu'à la promotion explicite de ces
changements vers beta ; la stable sera utilisable sur les nouveaux tags portant
la nouvelle configuration, une fois celle-ci promue vers main. La réactivation
de la CI seule ne rejoue pas les anciens événements.

## Protections solo

Main et beta : PR obligatoire, zéro approbation requise, contrôle `quality`
obligatoire, administrateurs inclus, force-push et suppression interdits.
Dev et legacy-auth : pushes directs permis, force-push et suppression interdits.
Engine_v1 et la sauvegarde de main : branches verrouillées, force-push et
suppression interdits, administrateurs inclus.

Le commit de migration CI/CD sur dev utilise `[skip ci]` : validation ciblée
uniquement, sans publication ni suite applicative complète. Le prochain push
de développement normal déclenchera la CI. Le déplacement de main peut lancer
l'ancienne CI ; son exécution doit être annulée pendant la migration.

## Prérequis avant promotion V2

Analyse limitée aux sept commits exclusifs de main et aux six commits de
legacy-auth absents de beta, identifiés lors de l'audit précédent :

- `e053f17`, `ac47fd5`, `505a869`, `0705a3d` : synchronisations de version,
  sans correctif fonctionnel à reporter.
- `1bfd5d1` : équivalent de patch de `d1af856` dans legacy-auth. Dans beta,
  onze des douze fichiers sont identiques ; le fichier restant a évolué.
  Confirmer le parcours de récupération des releases et d'affichage des mises à jour.
- `235e61f` : équivalent de patch de `980112d` dans legacy-auth ; SkiaSharp
  et SQLite mis à jour sont présents dans beta. Les fichiers de posters ont
  ensuite évolué : ne pas cherry-picker aveuglément sur le moteur V2.
- `f4ed623` : beta conserve Vite 7.3.7 et semver 6.3.1 contre 7.3.8 et 6.3.2
  dans ce correctif de main. Reporter les mises à jour nécessaires et vérifier
  les dépendances avant promotion ; aucune correction applicative n'est incluse ici.
  Les versions ciblées de react-router, js-yaml, postcss et nanoid sont déjà présentes.
- `09aab26`, `77051f4`, `a79abb0` : sessions sécurisées, login/logout, setup
  et navigation PWA ont des implémentations différentes dans V2. L'équivalence
  fonctionnelle complète n'est pas établie : vérifier cookies, CSRF, session,
  retours vers setup et comportement iOS/PWA.
- `4e27b02` : SmartAuthPolicy et CaptureConnection ont disparu de beta.
  Démontrer la protection équivalente contre le contournement de l'authentification
  via proxy et en-têtes transférés. Leur suppression seule ne prouve pas une faille.
- `d1af856` : voir le correctif de releases ci-dessus.
- `944c66b` : le test V1 ajoute explicitement GET pour l'accès public au shell.
  Les tests de setup V2 diffèrent ; vérifier le contrat équivalent et l'interdiction
  des chemins privés ou méthodes non autorisées.

Aucun changement du moteur V2, aucune migration SQLite ni opération NAS.

## Rollback de main

Suspendre les workflows de publication et terminer/annuler leurs runs actifs.
Lire la tête distante actuelle. Si elle diffère de la référence migrée, arrêter
et préserver ses nouveaux commits avant toute restauration.

La protection interdit volontairement le force-push et les pushes sans PR : un
administrateur doit sauvegarder sa configuration, lever temporairement ces
exigences (et les contrôles requis si nécessaire), exécuter la commande suivante,
puis rétablir immédiatement toute la protection et revérifier les références.
Autoriser seulement le force-push ne suffit pas lorsque les PR sont obligatoires.

```bash
git push \
  --force-with-lease=refs/heads/main:944c66bb231dded6ad4af6e88c7bcbfd1ce180b4 \
  origin 0705a3d9dcd087d7ab42120c8aba4469a36b8d85:refs/heads/main
```

Conserver beta, dev, engine_v1 et la sauvegarde. Garder les anciens workflows
de publication désactivés, y compris après restauration. Aucun tag n'est à modifier.
