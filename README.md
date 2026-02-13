<p align="center">
  <img src="docs/screenshots/logo.png" alt="Feedarr Logo" width="120" />
</p>

<h1 align="center">Feedarr</h1>

<p align="center">
  Smart release dashboard for Torznab, Jackett and Prowlarr.
</p>

<p align="center">
  English (default) | <a href="README.fr.md">Français</a>
</p>

<p align="center">
  <a href="https://github.com/Guizmos/Feedarr/actions/workflows/docker-publish.yml">
    <img src="https://github.com/Guizmos/Feedarr/actions/workflows/docker-publish.yml/badge.svg" alt="Build Status" />
  </a>
  <a href="https://github.com/Guizmos/Feedarr/releases">
    <img src="https://img.shields.io/github/v/release/Guizmos/Feedarr" alt="Latest Release" />
  </a>
  <a href="https://hub.docker.com/r/guizmos/feedarr-api">
    <img src="https://img.shields.io/docker/pulls/guizmos/feedarr-api?label=feedarr-api&logo=docker" alt="Docker Pulls API" />
  </a>
  <a href="https://hub.docker.com/r/guizmos/feedarr-web">
    <img src="https://img.shields.io/docker/pulls/guizmos/feedarr-web?label=feedarr-web&logo=docker" alt="Docker Pulls Web" />
  </a>
  <a href="LICENSE">
    <img src="https://img.shields.io/github/license/Guizmos/Feedarr" alt="License" />
  </a>
</p>

<p align="center">
  <a href="#features">Features</a> |
  <a href="#screenshots">Screenshots</a> |
  <a href="#installation">Installation</a> |
  <a href="#configuration">Configuration</a> |
  <a href="#development">Development</a> |
  <a href="#support">Support</a> |
  <a href="#license">License</a>
</p>

## Features

- Aggregate Torznab feeds from Jackett and Prowlarr.
- Parse titles and enrich releases with TMDB, TVmaze, Fanart and IGDB metadata.
- Display releases in a poster-based library with filters and details.
- Manage providers, indexers and categories from the UI.
- Backup, restore and maintenance workflows built into the API.
- Sonarr/Radarr integration for library and status workflows.
- Setup Wizard for first-run onboarding.

## Screenshots

<table>
  <tr>
    <td><img src="docs/screenshots/light_details.png" alt="Light - Details" width="320" /></td>
    <td><img src="docs/screenshots/light_stat.png" alt="Light - Stats" width="320" /></td>
    <td><img src="docs/screenshots/light_radarr.png" alt="Light - Radarr" width="320" /></td>
  </tr>
  <tr>
    <td><img src="docs/screenshots/dark_application.png" alt="Dark - Applications" width="320" /></td>
    <td><img src="docs/screenshots/dark_providers.png" alt="Dark - Providers" width="320" /></td>
    <td><img src="docs/screenshots/dark_indexeurs.png" alt="Dark - Indexers" width="320" /></td>
  </tr>
  <tr>
    <td><img src="docs/screenshots/dark_library.png" alt="Dark - Library" width="320" /></td>
    <td><img src="docs/screenshots/dark_fournisseur.png" alt="Dark - Provider Details" width="320" /></td>
    <td><img src="docs/screenshots/dark_top.png" alt="Dark - Top Releases" width="320" /></td>
  </tr>
</table>

## Installation

### Docker Compose

Prerequisites:
- Docker
- Docker Compose or Portainer stack support

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

Start:

```bash
docker compose up -d
```

Default endpoints:
- Web: `http://localhost:8888`
- API: `http://localhost:9999`

Optional external network (if your environment requires it):

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

### Runtime Configuration

Common API environment variables:
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

Feedarr includes a 6-step Setup Wizard (`/setup`) for first-run configuration.

- Direct configuration pages (Web UI):
  - Wizard: `http://localhost:8888/setup`
  - Settings (general): `http://localhost:8888/settings`
  - UI settings: `http://localhost:8888/settings/ui`
  - Metadata providers: `http://localhost:8888/settings/providers`
  - External services: `http://localhost:8888/settings/externals`
  - Applications (Sonarr/Radarr): `http://localhost:8888/settings/applications`
  - Users & auth: `http://localhost:8888/settings/users`
  - Maintenance: `http://localhost:8888/settings/maintenance`
  - Backup/restore: `http://localhost:8888/settings/backup`
  - Indexers: `http://localhost:8888/indexers`

- Full step-by-step guides (with screenshots and API key instructions):
  - English (default): `docs/configuration-wizard.md`
  - Français: `docs/configuration-wizard.fr.md`
- Includes:
  - Wizard flow, required fields and what each step impacts.
  - How to create/retrieve API keys for TMDB, Fanart, IGDB, TVmaze, Jackett, Prowlarr, Sonarr and Radarr.

## Development

### Requirements

- .NET SDK 8.x
- Node.js 18+

### Run Locally

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

### Validate Changes

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

## Security Notes

- API keys are stored using encrypted data-protection keys.
- Basic auth is available and configurable in the UI.
- Heavy stats endpoints are rate-limited.
- For WAN exposure, use TLS reverse proxy and enable auth.

## Support

- Documentation and setup notes: `README.md`, `README.fr.md`, `docker/`, `docs/configuration-wizard.md`, `docs/configuration-wizard.fr.md`
- Bug reports and feature requests: `https://github.com/Guizmos/Feedarr/issues`
- Releases: `https://github.com/Guizmos/Feedarr/releases`

## Contributing

- Open an issue before large feature changes.
- Keep pull requests focused and test-backed.
- Run backend/frontend checks locally before submitting.

## License

Licenses
- GNU GPL v3

Copyright
- Copyright 2026 Feedarr contributors

See `LICENSE` for full text.
