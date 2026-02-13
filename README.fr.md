<p align="center">
  <img src="docs/screenshots/logo.png" alt="Logo Feedarr" width="120" />
</p>

<h1 align="center">Feedarr</h1>

<p align="center">
  Tableau de bord intelligent pour Torznab, Jackett et Prowlarr.
</p>

<p align="center">
  <a href="README.md">English</a> | Français
</p>

<p align="center">
  <a href="https://github.com/Guizmos/Feedarr/actions/workflows/docker-publish.yml">
    <img src="https://github.com/Guizmos/Feedarr/actions/workflows/docker-publish.yml/badge.svg" alt="Statut Build" />
  </a>
  <a href="https://github.com/Guizmos/Feedarr/releases">
    <img src="https://img.shields.io/github/v/release/Guizmos/Feedarr" alt="Dernière Release" />
  </a>
  <a href="https://hub.docker.com/r/guizmos/feedarr-api">
    <img src="https://img.shields.io/docker/pulls/guizmos/feedarr-api?label=feedarr-api&logo=docker" alt="Docker Pulls API" />
  </a>
  <a href="https://hub.docker.com/r/guizmos/feedarr-web">
    <img src="https://img.shields.io/docker/pulls/guizmos/feedarr-web?label=feedarr-web&logo=docker" alt="Docker Pulls Web" />
  </a>
  <a href="LICENSE">
    <img src="https://img.shields.io/github/license/Guizmos/Feedarr" alt="Licence" />
  </a>
</p>

<p align="center">
  <a href="#fonctionnalites">Fonctionnalités</a> |
  <a href="#captures-decran">Captures d'écran</a> |
  <a href="#installation">Installation</a> |
  <a href="#configuration">Configuration</a> |
  <a href="#developpement">Développement</a> |
  <a href="#support">Support</a> |
  <a href="#licence">Licence</a>
</p>

## Fonctionnalités

- Agrégation des flux Torznab depuis Jackett et Prowlarr.
- Parsing des titres et enrichissement via TMDB, TVmaze, Fanart et IGDB.
- Affichage bibliothèque orienté posters avec filtres et détails.
- Gestion des providers, indexeurs et catégories depuis l'UI.
- Outils de maintenance, sauvegarde et restauration intégrés.
- Intégration Sonarr/Radarr pour les workflows bibliothèque et statut.
- Setup Wizard au premier lancement.

## Captures d'écran

<table>
  <tr>
    <td><img src="docs/screenshots/light_details.png" alt="Light - Détails" width="320" /></td>
    <td><img src="docs/screenshots/light_stat.png" alt="Light - Statistiques" width="320" /></td>
    <td><img src="docs/screenshots/light_radarr.png" alt="Light - Radarr" width="320" /></td>
  </tr>
  <tr>
    <td><img src="docs/screenshots/dark_application.png" alt="Dark - Applications" width="320" /></td>
    <td><img src="docs/screenshots/dark_providers.png" alt="Dark - Providers" width="320" /></td>
    <td><img src="docs/screenshots/dark_indexeurs.png" alt="Dark - Indexeurs" width="320" /></td>
  </tr>
  <tr>
    <td><img src="docs/screenshots/dark_library.png" alt="Dark - Bibliothèque" width="320" /></td>
    <td><img src="docs/screenshots/dark_fournisseur.png" alt="Dark - Fournisseurs" width="320" /></td>
    <td><img src="docs/screenshots/dark_top.png" alt="Dark - Top Releases" width="320" /></td>
  </tr>
</table>

## Installation

### Docker Compose

Prérequis:
- Docker
- Docker Compose (ou Portainer Stack)

```yaml
version: "3.9"

services:
  api:
    container_name: FEEDARR-API
    image: guizmos/feedarr-api:latest
    restart: unless-stopped
    environment:
      ASPNETCORE_URLS: http://+:8080
      App__DataDir: /app/data
    volumes:
      - /volume1/Docker/Feedarr/data:/app/data
    ports:
      - "9999:8080"

  web:
    container_name: FEEDARR-WEB
    image: guizmos/feedarr-web:latest
    restart: unless-stopped
    depends_on:
      - api
    ports:
      - "8888:80"
```

Démarrage:

```bash
docker compose up -d
```

Endpoints par défaut:
- Web: `http://localhost:8888`
- API: `http://localhost:9999`

Réseau externe optionnel (si nécessaire dans ton infra):

```yaml
services:
  api:
    networks:
      - docker_net
  web:
    networks:
      - docker_net

networks:
  docker_net:
    external: true
```

### Configuration Runtime

Variables d'environnement API les plus utiles:
- `ASPNETCORE_URLS`
- `App__DataDir`
- `App__DbFileName`
- `App__SyncIntervalMinutes`
- `App__RssLimit`
- `App__RssLimitPerCategory`
- `App__RssLimitGlobalPerSource`
- `App__Security__EnforceHttps`
- `App__Security__EmitSecurityHeaders`
- `App__RateLimit__Stats__PermitLimit`
- `App__RateLimit__Stats__WindowSeconds`
- `App__ReverseProxy__TrustedProxies__0`
- `App__ReverseProxy__TrustedNetworks__0`

## Configuration

Feedarr inclut un Setup Wizard en 6 étapes (`/setup`) pour la configuration initiale.

- Pages de configuration directes (Web UI):
  - Wizard: `http://localhost:8888/setup`
  - Réglages (général): `http://localhost:8888/settings`
  - Interface (UI): `http://localhost:8888/settings/ui`
  - Providers metadata: `http://localhost:8888/settings/providers`
  - Services externes: `http://localhost:8888/settings/externals`
  - Applications (Sonarr/Radarr): `http://localhost:8888/settings/applications`
  - Utilisateurs & auth: `http://localhost:8888/settings/users`
  - Maintenance: `http://localhost:8888/settings/maintenance`
  - Sauvegarde/restauration: `http://localhost:8888/settings/backup`
  - Indexeurs: `http://localhost:8888/indexers`

- Guide complet en français (par défaut):
  - [Français (par défaut)](docs/configuration-wizard.fr.md)
- Version anglaise:
  - [English](docs/configuration-wizard.md)

Le guide couvre:
- les actions à réaliser à chaque étape,
- les impacts concrets de chaque étape,
- la création/récupération des clés API pour TMDB, Fanart, IGDB, TVmaze, Jackett, Prowlarr, Sonarr et Radarr.

## Développement

### Pré-requis

- .NET SDK 8.x
- Node.js 18+

### Lancer en local

Backend:

```bash
dotnet run --project src/Feedarr.Api/Feedarr.Api.csproj
```

Frontend:

```bash
cd src/Feedarr.Web/feedarr-web
npm install
npm run dev
```

### Valider les changements

Backend:

```bash
dotnet test src/Feedarr.Api.Tests/Feedarr.Api.Tests.csproj -c Release
```

Frontend:

```bash
cd src/Feedarr.Web/feedarr-web
npm run lint
npm run test
npm run build
```

## Notes sécurité

- Les clés API sont stockées avec chiffrement via Data Protection.
- Une authentification Basic est disponible dans l'UI.
- Les endpoints statistiques lourds sont rate-limités.
- Pour une exposition WAN, utiliser un reverse proxy TLS et activer l'auth.

## Support

- Documentation: `README.md`, `README.fr.md`, `docs/configuration-wizard.md`, `docs/configuration-wizard.fr.md`
- Bugs / demandes: `https://github.com/Guizmos/Feedarr/issues`
- Releases: `https://github.com/Guizmos/Feedarr/releases`

## Contribution

- Ouvre une issue avant un changement fonctionnel important.
- Préfère des PR petites et testées.
- Lance les checks backend/frontend avant soumission.

## Licence

Licence:
- GNU GPL v3

Copyright:
- Copyright 2026 Feedarr contributors

Voir `LICENSE` pour le texte complet.
