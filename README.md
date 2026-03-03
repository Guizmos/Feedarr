<p align="center">
  <img src="docs/screenshots/logo.png" alt="Feedarr Logo" width="120" />
</p>

<h1 align="center">Feedarr</h1>

<p align="center">
  <strong>Feedarr v2 — Monolithic Architecture</strong><br/>
  Feedarr is now a single-container application.<br/>
  The previous split setup (<code>feedarr-api</code> + <code>feedarr-web</code>) is deprecated.
</p>

<p align="center">
  Smart release dashboard for Torznab, Jackett and Prowlarr.
</p>

<p align="center">
  English (default) | <a href="README.fr.md">Français</a>
</p>

<p align="center">
  <a href="https://github.com/Guizmos/Feedarr/actions/workflows/docker-release.yml">
    <img src="https://github.com/Guizmos/Feedarr/actions/workflows/docker-release.yml/badge.svg" alt="Build Status" />
  </a>
  <a href="https://github.com/Guizmos/Feedarr/releases">
    <img src="https://img.shields.io/github/v/release/Guizmos/Feedarr" alt="Latest Release" />
  </a>
  <a href="https://hub.docker.com/r/guizmos/feedarr">
    <img src="https://img.shields.io/docker/pulls/guizmos/feedarr?logo=docker" alt="Docker Pulls" />
  </a>
  <a href="LICENSE">
    <img src="https://img.shields.io/github/license/Guizmos/Feedarr" alt="License" />
  </a>
</p>

<p align="center">
  <a href="#features">Features</a> |
  <a href="#installation">Installation</a> |
  <a href="#configuration">Configuration</a> |
  <a href="#development">Development</a> |
  <a href="#security">Security</a> |
  <a href="#screenshots">Screenshots</a> |
  <a href="#support">Support</a> |
  <a href="#license">License</a>
</p>

---

## Features

- Aggregate Torznab feeds from Jackett and Prowlarr
- Metadata enrichment via TMDB, TVmaze, Fanart, IGDB and others
- Poster-based library view with advanced filters
- Provider, indexer and category management from the UI
- Sonarr/Radarr integration
- Built-in backup and restore system
- First-run Setup Wizard
- Secure credential storage (encrypted at rest)
- Optimized SQLite engine with connection pooling

---

## Installation

### Docker Compose (Monolithic)

Prerequisites:

- Docker
- Docker Compose or Portainer stack support

```yaml
version: "3.9"

services:
  feedarr:
    container_name: FEEDARR
    image: guizmos/feedarr:latest
    restart: unless-stopped
    environment:
      ASPNETCORE_URLS: http://+:8080
      App__DataDir: /app/data
    volumes:
      - /volume1/Docker/Feedarr/data:/app/data
    ports:
      - "8888:8080"
```

Start:

```bash
docker compose up -d
```

Default endpoints:

- Web UI: `http://localhost:8888`
- API: `http://localhost:8888/api`
- Health: `http://localhost:8888/health`

---

## Reverse Proxy

Feedarr runs as a single upstream service.

Point your reverse proxy to:

- `http://feedarr:8080`
- or `http://<host-ip>:8888`

Separate `/api` routing is no longer required.

HTTPS redirection should be handled by the reverse proxy.

---

## Configuration

On first run, Feedarr remains locked until the Setup Wizard (`/setup`) is completed.

For WAN deployments:

- Enable authentication
- Use a TLS reverse proxy

Direct configuration pages:

- Setup: `/setup`
- General settings: `/settings`
- Providers: `/settings/providers`
- External services: `/settings/externals`
- Applications: `/settings/applications`
- Users: `/settings/users`
- Backup/restore: `/settings/backup`
- Indexers: `/indexers`

Full step-by-step guides:

- [English (default)](docs/configuration-wizard.md)
- [Français](docs/configuration-wizard.fr.md)

---

## Development

### Requirements

- .NET SDK 8.x

### Run Locally (Monolithic)

```bash
dotnet run --project src/Feedarr.Api/Feedarr.Api.csproj -p:BuildWeb=true
```

Backend only:

```bash
dotnet run --project src/Feedarr.Api/Feedarr.Api.csproj
```

---

## Upgrade from Split Version

If previously using `feedarr-api` + `feedarr-web`:

1. Stop old containers
2. Remove both services
3. Replace with single `feedarr` service
4. Keep existing `/app/data` volume
5. Restart

No data migration required.

---

## Security

- All external provider API keys are encrypted at rest
- Sources, Providers and ARR applications use unified encryption
- Backup restore supports both legacy and current schema versions
- Legacy plaintext credentials are automatically normalized during restore
- SQLite foreign keys enforced
- Critical endpoints rate-limited
- Designed for reverse proxy TLS deployments

---

## Performance

- SQLite connection pooling enabled
- Async hot paths optimized
- Reduced redundant indexing
- Improved restore and migration stability

---

## Screenshots

<table>
  <tr>
    <td><img src="docs/screenshots/library.png" width="320" /></td>
    <td><img src="docs/screenshots/details_modal.png" width="320" /></td>
    <td><img src="docs/screenshots/providers.png" width="320" /></td>
  </tr>
</table>

---

## Support

- Issues: https://github.com/Guizmos/Feedarr/issues
- Releases: https://github.com/Guizmos/Feedarr/releases
- Documentation: `docs/`

---

## License

GNU GPL v3  
See `LICENSE` for details.