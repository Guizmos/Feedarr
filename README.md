Feedarr
=======

Feedarr est une application self‑hosted pour agréger des flux Torznab/Jackett/Prowlarr et afficher une bibliothèque de releases avec posters.

Fonctionnalités
---------------
- Synchronisation automatique des flux RSS Torznab.
- Parsing des titres (films/séries/jeux) et enrichissement des métadonnées.
- Affichage en grille avec posters locaux (TMDB/Fanart/IGDB).
- Modals de détails et actions rapides.
- Gestion des indexers, logs, et paramètres avancés.

Architecture
------------
- Backend: .NET 8 (API)
- Frontend: React + Vite
- Base de données: SQLite

Structure du dépôt
------------------
- Feedarr.sln
- src/Feedarr.Api: API .NET
- src/Feedarr.Web/feedarr-web: Frontend Vite
- docker: Dockerfile + docker-compose

Prérequis
---------
- .NET SDK 8.x
- Node.js 18+
- (optionnel) Docker

Configuration
-------------
Les clés externes (TMDB/Fanart/IGDB) se configurent dans l’UI:
- Paramètres > Externals

Sans clé, les posters peuvent être absents.

Lancement (dev)
--------------
Backend:
- Ouvrir la solution Feedarr.sln
- Lancer Feedarr.Api

Frontend:
- Aller dans src/Feedarr.Web/feedarr-web
- Installer les dépendances: npm install
- Démarrer: npm run dev

Docker
------
Voir docker/docker-compose.yml

Notes
-----
- Les posters sont stockés localement côté API (dossier data/posters).
- Les badges, logs et options UI sont configurables via l’UI.

Licence
-------
Non spécifiée.
