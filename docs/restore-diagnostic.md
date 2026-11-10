# Diagnostic P1 — conteneur Synology, SHA 304572d

La version de production déclarée est `v2.5.18-beta-sha.304572d`, branche beta,
Docker sur Synology/Portainer, exposée sur `https://feedarr.dalg.ovh`.
Le code de ce SHA a été relu. Aucune cause P1 n'est affirmée sans les résultats réels.
Le commit P2 `6219273` reste local et n'est pas poussé.

## Exécution unique

Prérequis : Python **3.8+** et CLI Docker sur le Synology, avec le droit de lire le
conteneur et d'y exécuter des commandes de diagnostic. Aucune installation n'est faite
par le script. Copier uniquement `docker/diagnostics/diagnose-restore.py` sur le NAS, relever le
nom exact du conteneur dans Portainer, puis lancer dans son dossier :

```sh
sudo python3 -B diagnose-restore.py NOM_EXACT_DU_CONTENEUR
```

Remplacer `NOM_EXACT_DU_CONTENEUR` par le nom Portainer. Le script n'invente pas de nom,
ne choisit pas automatiquement un conteneur et ne crée aucun rapport sur disque.
`-B` empêche l'écriture de cache Python. La sortie est du JSON, une observation par
ligne. Transmettre toute cette sortie ; les logs bruts, `docker inspect` complet,
variables d'environnement complètes et sauvegardes ne sont pas nécessaires.
Si Python ou l'accès Docker manquent, transmettre uniquement le message d'erreur ;
ne pas installer ou redémarrer quoi que ce soit pour contourner cela.

Ne pas relancer une restauration avant d'avoir interprété le rapport. Il est possible
d'exécuter ce diagnostic sur une opération actuellement bloquée sans cliquer à nouveau
sur « Confirmer ». Si le blocage a cessé, les fichiers et logs résiduels sont relevés.

## Observations recueillies

- Identifiant immuable de l'image, version embarquée dans l'image et version du
  conteneur, révision OCI si disponible, état, démarrage, compte de redémarrages/OOM.
- Résolution du DataDir depuis JSON, environnement et arguments standard du processus.
  Chemins DataDir et montages sont affichés volontairement pour localiser les fichiers
  sur le NAS ; les noms de sauvegardes, posters et contenus applicatifs sont exclus.
- Montage persistant couvrant DataDir, éventuels montages imbriqués, lecture/écriture du
  montage, modes/UID/GID/inodes/tailles/dates et capacité disque. Les permissions sont
  aussi interrogées avec `test -r/-w/-x` sous l'UID/GID effectif de l'application, sans
  tentative d'écriture. Ce test ne reproduit pas ses groupes supplémentaires.
- `pending.json`, temporaire de publication, journal et résultat : structure JSON,
  identités GUID, date, compteurs, correspondance manifeste/staging et checksum de
  `incoming.db` préparé. Les valeurs de checksum DB ne sont pas affichées.
- Inventaire borné des dossiers `building-*`, `stages` et `rescue`, sans suppression.
  Les liens symboliques sont refusés pour les lectures et le checksum du staging.
- Deux GET exclusivement : `/api/system/backups/state` et
  `/api/system/backups/restore-state`, depuis le conteneur puis via l'URL publique,
  sans identifiants/cookies et sans suivre les redirections. Les réponses sont
  réduites aux statuts, enums connus, booléens et compteurs ; aucun corps brut.
- Comparaison par empreinte du document HTML embarqué et de celui servi publiquement.
  Une différence ne prouve pas un cache obsolète : auth, setup et proxy peuvent
  également changer la page. Le contenu HTML n'est pas affiché.
- Logs Docker limités aux 5 000 dernières lignes des sept derniers jours. Uniquement
  catégories connues, types d'exceptions connus, méthodes connues et horodatages
  pertinents sont affichés. Les messages, chemins de stack traces et logs bruts sont
  toujours masqués, y compris les erreurs de commandes.

## Limites et garanties

Aucun POST/DELETE, aucune préparation, annulation, suppression, écriture dans le
conteneur, modification de permissions ou redémarrage. Aucune ouverture SQLite directe,
aucune vérification d'intégrité qui pourrait créer des fichiers SQLite auxiliaires.
Seul le checksum du fichier SQLite préparé est lu ; aucune sauvegarde ZIP ni base active
n'est lue. Les GET peuvent produire les logs d'accès habituels du serveur.

Les requêtes anonymes peuvent retourner 401/403/302 : le script le signale sans tenter
de contourner l'authentification. Un GET actuel ne permet pas de reconstituer la réponse
du POST de restauration passé. Une capture ciblée de l'onglet Réseau pourra ensuite
être nécessaire, sans exporter cookies ni headers Authorization.

Les lectures sont successives et non atomiques, bornées à 4 Mio par document/flux de
log. Les inventaires détaillés sont limités à 30 dossiers de chaque catégorie. Un gros
checksum de staging peut prendre jusqu'à deux minutes ; les autres commandes ont des
délais plus courts. Le script n'ouvre aucun verrou applicatif. Il ne garantit donc pas
un snapshot immuable pendant une préparation en cours.

## Validation locale

```sh
python3 -B -m unittest discover -s docker/diagnostics/tests -v
```

Quatre tests réussis : rapport simulé avec staging valide et données contenant des
secrets factices, masquage des erreurs/réponses invalides, refus des liens symboliques
et chemins contenant du texte shell, gestion d'un timeout sans fuite de message.
Les tests ne contactent ni Docker réel ni l'instance de production. `git diff --check`
réussit. La validation réelle sur Synology reste à faire par l'utilisateur.

## Résultat NAS reçu — complément ciblé

Le premier rapport confirme l'image et le conteneur au SHA `304572d`, un bind mount
persistant `/volume1/Docker/Feedarr/data` vers `/app/data`, les droits attendus de
l'UID/GID 10001 et de l'espace disque disponible. Aucun pending, journal, résultat,
stage ou building n'est présent lors de l'observation. Il ne faut pas confondre les
probes `readable: false` d'un fichier absent avec un défaut de permissions.

Les logs recensent deux échecs de préparation, le 10 novembre 2026 à
11:15:54.757962016 UTC et 11:16:31.462477563 UTC. Ils recensent aussi des exceptions
`OperationCanceledException` et des frames `PendingRestoreStore.Publish`. L'ancien
résumé ne conservait pas l'association exacte exception/frame/numéro de ligne ; il ne
permet pas encore d'identifier le point d'annulation et son déclencheur. Les quatre
GET de statut retournent 401, donc aucun état JSON backend n'a été observé par HTTP.

Le script actualisé ajoute uniquement l'extraction filtrée des frames et numéros de
ligne, des exceptions proches de chaque échec de préparation et des frames de contrôle
d'annulation. Une limite de bloc sépare les nouveaux enregistrements de logs ; des
messages entrelacés peuvent néanmoins empêcher une corrélation parfaite. Il ne montre
ni arguments de méthode, ni chemins de stack traces, ni messages bruts.

Recopier le script actualisé sur le NAS et lancer seulement :

```sh
sudo python3 -B diagnose-restore.py FEEDARR --logs-only
```

Ce mode ne fait que `docker logs` en lecture ; aucun `docker exec`, GET ou accès au
staging. Aucune nouvelle préparation n'est nécessaire. Il s'agit des logs existants,
avec les mêmes limites de sept jours/5 000 lignes. Cinq tests locaux réussissent,
dont un cas de stack d'annulation avec secrets factices et numéros de ligne.

Sur le SHA concerné, `Publish` contrôle l'annulation dans `CopyTree` (ligne 331,
appel ligne 92 de `Publish`) et après génération du manifeste de travail (ligne 102).
Ces deux points précèdent la publication de `pending.json` (ligne 115). Le `finally`
de `BackupService` supprime le work non publié. Ce parcours explique comment une
préparation annulée peut ne laisser aucun fichier, sans redémarrage ni perte du volume.
Il ne démontre pas si l'annulation vient d'un refresh, du client, du proxy ou d'un autre
événement ; ni pourquoi l'attente initiale aurait été excessive.

Le bandeau orange « Opération en cours: prepare-restore (running) » est un indicateur
`isBusy` dans `SettingsBackup`, distinct du bandeau global « Restauration en attente »
issu de `pending.json`. Le rapport actuel ne permet pas d'identifier lequel a été vu.
Aucun changement du fonctionnement P1 n'est appliqué avant ces précisions.

## Second rapport : annulation localisée dans la copie des posters

Le rapport `--logs-only` reçu associe chaque échec à `OperationCanceledException`,
`CancellationToken.ThrowIfCancellationRequested`, `CopyTree` ligne 331 et `Publish`
ligne 92. Le second comporte aussi la récursion `CopyTree` ligne 337. Les deux
préparations ont donc échoué dans la copie des posters, avant publication du manifeste.
Le nettoyage du work dans `finally` explique l'inventaire vide. Aucune de ces deux
préparations n'a été appliquée à la base active.

Le déclencheur de l'annulation n'est pas établi. Le token provient de la requête HTTP ;
les résultats ne permettent pas de distinguer fermeture/refresh du navigateur, délai
client ou proxy. Ils ne prouvent pas non plus un deadlock : la copie atteint un point
de contrôle du token et lève l'exception attendue. L'utilisateur précise que le bandeau
apparaissait rapidement après le clic, mais que son texte était masqué par la modale
et le flou ; son identité reste inconnue.

Le code copie récursivement tous les posters et exécute `Flush(true)` pour chaque
fichier. Le volume à copier doit être quantifié avant d'attribuer la lenteur à cette
étape. Le script actualisé permet un relevé supplémentaire facultatif :

```sh
sudo python3 -B diagnose-restore.py FEEDARR --storage-summary
```

Le champ `poster_storage_metadata` contient uniquement le nombre de fichiers, la somme
de leurs tailles et la durée du parcours des métadonnées (maximum 90 secondes).
Les noms et contenus ne sont pas affichés ; aucun fichier n'est copié. Cette durée
n'est pas un benchmark de copie ni de fsync. Les autres observations du diagnostic
complet restent en lecture seule. Ne pas relancer une restauration pour ce relevé.
Si la requête POST d'origine reste dans l'onglet Réseau du navigateur, son statut et sa
durée suffisent ; ne transmettre ni cookies, ni headers d'authentification, ni HAR brut.
P1 reste inchangé et aucun commit de correction P1 n'est créé à ce stade.


## Correctif local préparé après réception des trois rapports

Le relevé supplémentaire mesure 52 293 fichiers et 6 613 367 029 octets de posters.
Les sections précédentes conservent les conclusions aux étapes du diagnostic, avant
correction. Le cycle de vie est désormais corrigé localement : acceptation HTTP 202
explicite, préparation possédée par le service hébergé et suivi jusqu'au manifeste
persistant. Voir le [rapport à jour](production-regressions-2026-11-10.md) pour les
preuves, tests et limites. Aucun changement n'a été déployé sur le NAS, aucun staging
n'a été effacé par l'agent et aucune nouvelle préparation de production n'a été lancée.
