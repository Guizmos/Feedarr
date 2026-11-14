# Sauvegarde et restauration de configuration

Dans **Paramètres → Sauvegarde / Restauration** :

- **Sauvegarder** : saisir un mot de passe de 12 à 1 024 caractères, le confirmer, puis télécharger le fichier `.feedarr`. Conserver ce mot de passe séparément : Feedarr ne l’enregistre pas et ne peut pas le récupérer.
- **Restaurer** : sélectionner ce fichier, saisir son mot de passe, cliquer sur **Analyser**, consulter les compteurs puis **Confirmer la restauration**. Une seule confirmation applique immédiatement la configuration.

Le fichier contient les paramètres généraux, UI, synchronisation/rétention, connexions Arr, indexeurs, sources, catégories sélectionnées/mappings et fournisseurs de métadonnées, avec leurs secrets. Il ne contient ni administrateur, ni sessions, ni politique HTTP/HTTPS, ni releases, entités, posters, historique, logs ou caches. Les réglages de déploiement par variables d’environnement restent propres à chaque instance.

La restauration conserve l’administrateur, ses sessions, la politique de transport et les données métier de la cible. Les connexions existantes absentes de l’archive sont désactivées, leurs lignes et relations historiques restent conservées. Les connexions correspondantes gardent leurs identifiants locaux ; les autres reçoivent de nouveaux identifiants, avec remappage des références. Les destinations restent soumises à la politique SSRF de la cible.

Une analyse ne modifie rien. L’application relit et authentifie le fichier, vérifie que la configuration n’a pas changé, attend la fin des activités de synchronisation via le coordinateur existant, puis utilise une transaction SQLite immédiate avec `synchronous=FULL`. Toute erreur avant le commit annule l’ensemble ; un arrêt brutal laisse SQLite récupérer sa transaction au prochain démarrage. Aucune archive ou opération différée n’est stockée sur le serveur.

Les secrets sont rechiffrés avec le trousseau de la cible. Les paramètres sont relus par les services existants ; seuls les caches de découverte des indexeurs et d’état système sont invalidés. Les caches de catégories suivent déjà l’empreinte de connexion. Un message signale le redémarrage nécessaire pour les workers posters, les limites de concurrence initialisées au démarrage ou un changement des identifiants IGDB (jeton OAuth conservé en mémoire). Le message reste vrai pendant la durée de vie du processus si plusieurs restaurations se suivent. Aucun redémarrage automatique n’est imposé.

## Format et API

Un seul format courant, version 3, sans ZIP ni copie SQLite :

```text
FEEDARR-CONFIG-3\nPBKDF2-SHA256:600000\nAES-256-GCM\n
sel aléatoire (16 octets) | nonce aléatoire (12) | JSON chiffré | tag GCM (16)
```

PBKDF2-SHA256, 600 000 itérations, clé de 256 bits. L’en-tête, le sel et le nonce sont authentifiés comme données associées. Le document suit un schéma explicite et des listes de champs autorisés ; taille de configuration limitée à 8 Mio. Un mauvais mot de passe ou une altération est refusé avant toute modification.

Trois POST administrateur, avec session/CSRF (ou authentification API existante) et limitation des tentatives :

- `/api/configuration-backups/export` : JSON `password`, `confirmation` → téléchargement complet.
- `/api/configuration-backups/preview` : multipart `file`, `password` → compteurs et empreinte de l’état cible.
- `/api/configuration-backups/restore` : mêmes champs + `expectedState`, `confirm=true` → résultat après commit.

L’empreinte de prévisualisation n’est ni un secret ni un jeton d’autorisation. Elle évite d’appliquer une analyse devenue obsolète. Les mots de passe et contenus privés ne figurent jamais dans les URLs, récapitulatifs ou erreurs.

Les anciens formats et endpoints de sauvegarde complète, bootstrap, staging, reprise et première synchronisation spéciale ont été supprimés. Les anciennes migrations SQLite sont conservées ; la migration 0062 retire uniquement la table d’opérations différées. Les anciens fichiers d’archives/staging sur disque ne sont ni importés ni effacés automatiquement.

## Vérifications reproductibles

Depuis le worktree permanent `Feedarr-beta` :

```sh
dotnet test src/Feedarr.Api.Tests/Feedarr.Api.Tests.csproj
dotnet build src/Feedarr.Api/Feedarr.Api.csproj -c Release
cd src/Feedarr.Web/feedarr-web
npm ci
npm test
npm run lint
npm run build
npm run test:initial-admin-http
```

Les tests utilisent leurs propres répertoires temporaires, bases SQLite, trousseaux et ports. Le dernier script lance de vrais processus ASP.NET, teste HTTP et TLS, les formulaires React dans jsdom, l’import entre instances, une erreur SQL injectée et un `SIGKILL` pendant une transaction. Il arrête ses processus et supprime ses données isolées. Les tests ne contactent aucune intégration externe réelle. La validation DOM ne remplace pas une vérification visuelle dans un navigateur.
