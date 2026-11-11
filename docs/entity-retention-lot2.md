# Lot 2 — rétention conservatrice des entities et posters historiques

Branche isolée : `fix/retention-lot2`, base `origin/beta` au commit `9d76b41`.
Worktree : `/private/tmp/feedarr-retention-lot2-20261110`.
Aucun accès en écriture, nettoyage, requête applicative ou déploiement sur le Synology.
Aucun fichier Backup V2, wizard, Program ou dépendance de package modifié.

## Diagnostic limité aux chemins concernés

`EnsureMediaEntitiesForReleases` crée/lie les entities lors des upserts ; les chemins
Rebind/Rename créent ou réutilisent leur identité. `ApplyRetention`, les suppressions
de releases et les rebinds enlèvent les liens mais ne supprimaient pas les entities.
Leurs `poster_file` et `poster_store_dir` continuaient donc de protéger les fichiers.
Le lot 1 a correctement conservé cette protection : enlever une entity historique
exige une décision métier distincte du GC physique.

L'inventaire fourni relève 100 356 entities dont 97 578 sans release et 45 319 posters
protégés uniquement par ces dernières. Cela ne prouve pas leur éligibilité.
`media_entity_arr_status` conserve des associations ; `request_tmdb_*` conserve des
identités de requête. Une correction de titre peut rebinder une entity ; les anciens
schémas ne conservaient pas une provenance manuelle fiable une fois la release
supprimée. Les caches `poster_matches`, y compris manuels, constituent aussi des
références à préserver.

Les **51 004** de l'audit désignent des **noms distincts de stores déclarés en base
sans répertoire correspondant**, et non 51 004 fichiers à supprimer. La migration
0050 préremplit des identités de stores sans matérialiser leurs répertoires. Certaines
références peuvent donc être logiques/historiques ; l'inventaire seul ne permet pas
d'attribuer l'origine de chacune, notamment après d'anciens GC. Le nouveau rapport
sépare noms distincts manquants et nombre de lignes qui les déclarent. Il ne répare,
ne recrée et ne vide aucun de ces champs automatiquement.

## Critères exacts et provenance

La migration 0056 ajoute `retention_origin` et `orphaned_at_ts` :

- Toutes les anciennes entities ont par défaut `legacy_unknown` et restent protégées.
  Les upserts ne transforment jamais une ancienne provenance inconnue en automatique.
- Seules les nouvelles créations automatiques marquent explicitement `automatic`.
- Renommage manuel, release avec `title_manual_override`, sélection manuelle de poster :
  la provenance manuelle est conservée. Aucune modification des moteurs de matching,
  des appels fournisseurs, des images téléchargées ou du routage TMDB/TVMaze/RAWG/IGDB.
- Les triggers de liaison/déliaison enregistrent la dernière disparition de la dernière
  release. Une réutilisation remet le délai à zéro. L'ancienneté de création seule
  ne rend jamais une entity éligible.

Une entity est candidate uniquement si **toutes** ces conditions sont vraies :

1. Provenance explicitement `automatic`.
2. Aucune release liée, aucune ligne Arr (même négative), aucune autre référence
   conventionnelle `entity_id`/`media_entity_id` ou clé étrangère déclarée détectée.
3. Aucun `request_tmdb_id`, statut ou timestamp de résolution de requête.
4. Aucune trace manuelle de poster/cache par fichier ou identité titre/année.
5. Aucune donnée non vide dans une nouvelle colonne d'entity non reconnue.
6. Horodatages valides, sans release depuis au moins **30 jours** ; création,
   dernière modification et métadonnées/poster également hors de cette période.

Les identifiants et métadonnées fournisseurs d'une entity explicitement automatique
sont un cache reconstructible ; les mêmes données d'une entity historique inconnue
ne sont jamais présumées reconstructibles. L'absence de preuve protège la ligne.
**Le lot ne permet donc pas de purger les 97 578 anciennes entities par défaut.**
Un gain d'espace sur ce sous-ensemble ambigu n'est pas promis. Il faudrait une preuve
ou une revue distincte de provenance, pas un contournement du garde-fou.

## Exécution et concurrence

- Aucun appel automatique au nouveau nettoyage depuis la rétention/synchronisation.
- Simulation par défaut. Exécution réelle seulement avec `dryRun:false` et confirmation
  explicite ; pas de paramètre force ni de réduction du délai de grâce.
- 100 entities et 100 tentatives de suppression de posters maximum par appel.
  Transactions entities par groupes de 10, avec relecture de l'ensemble des critères
  sous transaction SQLite **IMMEDIATE**, juste avant DELETE.
- Lecture initiale hors verrou d'écriture. Les transactions réelles ont un timeout
  SQLite de contention réduit à 500 ms et s'arrêtent en cas d'échec de lot.
- Les publishers restent concurrents entre eux. Un collector ne les attend pas :
  si une publication est active, il reporte le travail. Une nouvelle publication
  attend uniquement la courte transaction/suppression courante.
- Les points d'entrée de publication de posters et le calcul/enregistrement du statut
  Arr prennent cette lease autour de leurs lectures/publications pour éviter la course
  lecture d'une entity / suppression / publication tardive d'une association.
- Les verrous par store du lot 1 restent inchangés. Ce lot ne supprime pas de stores.
  La couche `IPosterFileStore` et le même contrôle transactionnel des références sont
  partagés avec le nettoyage ciblé existant des posters plats ; pas de second moteur
  de suppression.
- L'exécution réelle utilise le coordinateur d'opérations existant pour ne pas recouvrir
  une préparation de backup/restore, sans bloquer les synchronisations normales.
  Un pending, journal, staging incomplet ou ancien flag de restauration reporte toute
  suppression. Les répertoires de staging/rescue ne sont jamais parcourus par le GC.

## Fichiers historiques

Seuls les fichiers à plat dans `posters/` sont examinés. La liste blanche correspond
aux noms automatiques numériques connus : TMDB `-w500`, IGDB/RAWG `-cover`, TVMaze
`-original`/`-medium`, Fanart/Jikan/ComicVine/TheAudioDB numériques, extensions
jpg/jpeg/png/webp. Identifiants strictement positifs, sans zéros initiaux.

Les noms manuels/inconnus/autres fournisseurs, fichiers vides/atypiques, liens symboliques,
fichiers récents et **toutes** les références base (releases, entities, cache et autres
tables comportant `poster_file`) sont conservés. Un fichier ancien libéré est recontrôlé
après acquisition de la lease, puis ses références sont relues dans une transaction
IMMEDIATE immédiatement avant `IPosterFileStore.Delete`. Les références de cache ne
sont pas effacées pour fabriquer artificiellement des candidats.

Le scan retient au plus `scanLimit+1` noms en mémoire, inspecte au maximum 1 000 fichiers
et 1 000 stores par défaut (plafond 5 000), et renvoie des curseurs. L'énumération des
noms du répertoire reste nécessaire pour fournir une fenêtre ordonnée ; les contenus
images ne sont pas lus. Les compteurs d'entities sont globaux, ceux des fichiers/stores
et l'estimation d'octets sont ceux de la fenêtre. Les curseurs évoluent avec les données
concurrentes : ce n'est pas un snapshot global immuable.

L'interruption après suppression d'entities laisse des fichiers non référencés qui
peuvent être repris au passage suivant. Les suppressions d'entities et leur événement
`activity_log` sont atomiques. Une panne entre unlink et commit de l'audit d'un fichier
peut empêcher de comptabiliser cet unlink dans le rapport, mais ne crée pas de perte
de fichier référencé : le verrou et la vérification précèdent toujours l'unlink.
Les erreurs ne sont pas masquées et les compteurs de suppression ne sont incrémentés
qu'après confirmation. Aucun VACUUM ni téléchargement/recréation de poster.
Les octets sont des tailles logiques, pas une garantie d'espace libéré avec Btrfs,
compression, hardlinks ou snapshots Synology.

## API et maintenance

- `GET /api/maintenance/history-cleanup?limit=100&scanLimit=1000` : dry-run uniquement.
- `POST /api/maintenance/history-cleanup` sans option : dry-run également.
- Réel : `dryRun:false`, `confirmation:"RECLAIM_UNUSED_HISTORY"`, limites bornées.
- `afterPoster`/`afterStore` avancent séparément les fenêtres. Le rapport renvoie
  `nextPoster`, `nextStore`, `windowAfterPoster`, `windowAfterStore`.
- Paramètres/confirmation invalides : 400. Maintenance/restore occupé : 409 ou rapport
  de protection. Erreurs détaillées dans les journaux, jamais de `ex.Message` brut
  dans les réponses de contrôleur.
- Interface Maintenance : « Analyser sans supprimer ». Checkbox, confirmation,
  verrou anti-double clic et plafond obligatoires pour le réel. Une exécution réelle
  recontrôle la fenêtre analysée, pas un autre lot implicite.

Le dry-run utilise une connexion SQLite ReadOnly sans Db.Open(), sans création de
répertoire, migration, écriture d'activité ou marqueur de candidature. Les seules
lectures filesystem sont métadonnées/références. Les logs d'accès HTTP habituels du
serveur restent ceux de l'application, pas une mutation de l'historique par le dry-run.

## Validation

- Fixture dry-run : **1 entity, 1 poster, 4 octets estimés**, aucun changement des
  hashes ni des dates d'écriture des fichiers/base ; confirmation manquante : aucun
  changement non plus.
- Catalogue temporaire de **100 000 entities** : 25 entities et 25 fichiers supprimés,
  30 fichiers examinés, reprise par curseur sur les 20 restants ; exécution bornée.
- Tests : références release/entity/cache, Arr, requête, correction manuelle,
  provenance héritée, données/dépendances supplémentaires, délai réinitialisé,
  revalidation concurrente, publishers, interruption/reprise, symlinks, erreurs,
  tentatives plafonnées, restauration différée protégée, GET-only UI et confirmation.

La suite backend complète a été exécutée **une seule fois**, involontairement plus tôt
que prévu : le filtre `Arr` correspondait aussi au namespace `Feedarr`. Résultat :
**1 093 réussites et 1 échec** du garde-fou interdisant `ex.Message` dans le nouvel
endpoint. Cette exposition a été corrigée en réponse constante. La suite complète
n'a pas été relancée pour respecter la limite demandée. Après correction : **186 tests
ciblés réussis**, puis **51 contrôles finaux** rétention/GC et garde-fou réussis. Aucun
échec connu restant ; ne pas présenter la première exécution globale comme verte.

Frontend : **315/315** tests réussis. Release backend : zéro avertissement/zéro erreur.
Vite, ESLint et `git diff --check` réussis. Toutes les bases et images de test sont
créées dans des répertoires temporaires. Aucun test destructif de production.

## Premier dry-run proposé sur Synology — aucune suppression réelle

Après intégration contrôlée du commit sur beta et déploiement **décidé par l'utilisateur**
(aucun push/déploiement exécuté ici), avec la migration 0056 appliquée au démarrage :

1. Dans une session authentifiée, ouvrir **Paramètres → Maintenance → Historique des
   entities et posters** et cliquer seulement **Analyser sans supprimer**.
2. Conserver les plafonds 100/1 000. Relever compteurs globaux d'entities, raisons de
   protection, candidats physiques, octets logiques, erreurs, fichiers absents et stores
   manquants de cette fenêtre. Ne pas cocher l'activation du réel.
3. Utiliser **Analyser la fenêtre suivante** pour les curseurs ; ne pas additionner des
   fenêtres qui se recouvrent ou des compteurs globaux d'entities répétés.
4. Transmettre les rapports de simulation pour revue. Une absence de candidats avec
   beaucoup de `unknown_provenance` est attendue, pas un motif de lever les protections.

Équivalent GET, dans le même navigateur authentifié :
`/api/maintenance/history-cleanup?limit=100&scanLimit=1000`.
Aucun POST réel, script de purge SQL, changement de provenance en masse ou suppression
sur le NAS n'est proposé pour ce premier diagnostic.

## Intégration

Le chantier Backup V2 a avancé dans le worktree principal pendant ce lot (commit
`f67ffa4` observé). Il n'est ni inclus, ni rebasé, ni modifié par cette branche. Le
commit du lot 2 doit être intégré de manière contrôlée ; aucun push direct sur beta.
Une validation de l'ensemble fusionné appartient à cette intégration, distincte de
la validation du worktree isolé présentée ici.

## Intégration sur beta (2026-11-11)

Application du seul commit `2396c83` sur `c0534cb`, sans modification des fichiers Backup V2.
La migration Backup V2 reste `0055_configuration_restore_v2.sql` ; le GC devient
`0056_entity_retention.sql`. L’utilisateur confirme que la migration GC originale
n’a été exécutée que sur des bases temporaires, jamais persistantes.
Le garde-fou GC vérifie aussi les marqueurs Backup V2 `configuration-restore/plan.bin`
et `plan.bin.tmp` (fichiers ou répertoires), sans les modifier. Aucun appel automatique
au nettoyage historique n’est ajouté ; simulation par défaut et double confirmation
pour le réel restent obligatoires.

Validation de l’ensemble intégré :
- 136 tests backend ciblés réussis (GC/rétention, Backup V2, posters/Arr), dont
  quatre cas de protection contre les marqueurs de restauration V2.
- Suite backend complète exécutée une seule fois : 1 194 réussites, zéro échec/ignoré.
- Probe SQLite temporaire : ancien schéma 0054, état Backup V2 créé après 0055,
  application 0056 puis relance des migrations ; provenance legacy_unknown, référence
  de poster et opération Backup V2 préservées, integrity_check et foreign_key_check OK.
- Démarrage HTTP local sur cette ancienne base migrée : /health 200, historique
  conservé et zéro événement history_cleanup après arrêt. Aucune source distante.
- Frontend ciblé GC/Backup V2 : 44/44 ; Vite et ESLint des composants modifiés OK.
- Build backend Release : zéro avertissement, zéro erreur ; git diff --check OK.
- Aucun accès au NAS, changement Docker ou nettoyage réel hors fixtures temporaires.

La première tentative VSTest a été abandonnée avant exécution par la restriction
de socket du sandbox ; les validations ont ensuite utilisé le socket local autorisé.
Le probe temporaire a produit un avertissement NuGet NU1900 (audit de vulnérabilités
inaccessible depuis le sandbox), sans échec de migration ; le build Release est propre.
