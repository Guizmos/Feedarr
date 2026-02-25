<p align="center">
  <img src="docs/screenshots/logo.png" alt="Feedarr Logo" width="120" />
</p>

<h1 align="center">Feedarr</h1>

<p align="center">
  <strong>⚠️ Feedarr v2 introduces a major architectural change.</strong><br/>
  Feedarr is now a single-container monolithic application.<br/>
  The previous split setup (<code>feedarr-api</code> + <code>feedarr-web</code>) is deprecated and no longer maintained.
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

First run security behavior:
- Feedarr starts in `setup lock` mode until the setup wizard is completed.
- During this phase, only health/setup routes and setup-required assets/APIs are available.
- Other routes return `Setup required`.

Optional external network (if your environment requires it):

```yaml
services:
  feedarr:
    networks:
      - docker_net

networks:
  docker_net:
    external: true
```

### Reverse Proxy (NPM / Traefik / Caddy)

Feedarr now runs as a single upstream service.
Point your reverse proxy to:

 - `http://feedarr:8080`
 or
 - `http://<host-ip>:8888`

You no longer need separate rules for `/api` and `/`.

HTTPS redirection is handled by your reverse proxy.  
`App__Security__EnforceHttps` is disabled by default.

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
- `App__Updates__Enabled`
- `App__Updates__RepoOwner`
- `App__Updates__RepoName`
- `App__Updates__CheckIntervalHours`
- `App__Updates__TimeoutSeconds`
- `App__Updates__AllowPrerelease`
- `App__Updates__GitHubApiBaseUrl`
- `App__Updates__GitHubToken`

## Configuration

On first run, Feedarr requires completing the Setup Wizard (`/setup`) before unlocking the full UI/API.
- For WAN deployments, configure authentication after onboarding (`Settings -> Users`).
- `Authentication=none` is supported for LAN-only usage, but not recommended for WAN exposure.

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
  - About / updates: `http://localhost:8888/system/updates`
  - Indexers: `http://localhost:8888/indexers`

- Full step-by-step guides (with screenshots and API key instructions):
  - [English (default)](docs/configuration-wizard.md)
  - [Français](docs/configuration-wizard.fr.md)
- Includes:
  - Wizard flow, required fields and what each step impacts.
  - How to create/retrieve API keys for TMDB, Fanart, IGDB, TVmaze, Jackett, Prowlarr, Sonarr and Radarr.

## Development

### Requirements

- .NET SDK 8.x

### Run Locally (Monolithic)

Backend + UI (recommended):

```bash
dotnet run --project src/Feedarr.Api/Feedarr.Api.csproj -p:BuildWeb=true
```

Fast backend-only run (no UI rebuild):

```bash
dotnet run --project src/Feedarr.Api/Feedarr.Api.csproj
```

## Upgrade from Split Version (API + Web)

If you previously used separate `feedarr-api` and `feedarr-web` containers:

1. Stop old containers
2. Remove both services from docker-compose
3. Replace with the single `feedarr` service
4. Keep the same `/app/data` volume
5. Start again

No data migration is required.

If onboarding was not completed before upgrading, Feedarr will stay in setup lock mode until `/setup` is completed.

## Release Workflow and Updates

### Create/Update a GitHub Release

Use the PowerShell script (Windows PowerShell 5.1+ and PowerShell 7 supported):

```powershell
$env:GITHUB_TOKEN="ghp_xxx"
pwsh ./scripts/release.ps1 `
  -Version 5.4.0 `
  -RepoOwner Guizmos `
  -RepoName Feedarr `
  -GenerateNotes $true
```

Notes:
- Tag format is always `vX.Y.Z` (created locally if missing, then pushed).
- Script upserts the matching GitHub Release for the tag.
- If `gh` CLI is installed it is used first; otherwise script falls back to GitHub REST API.
- Use `-DryRun` to preview actions without changing GitHub.

### In-App Update Check

- Backend endpoint: `GET /api/updates/latest` (manual refresh: `?force=true`).
- Feedarr compares current build version to latest GitHub release tag (supports `v` prefix).
- By default prerelease tags are ignored unless `App:Updates:AllowPrerelease=true`.
- UI location: `Systeme -> A propos`.
- “Update available” red badge remains until changelog is opened/acknowledged (stored in localStorage per tag).

## Security Notes

- API keys are stored using encrypted data-protection keys.
- Basic auth is available and configurable in the UI.
- Heavy stats endpoints are rate-limited.
- `Authentication=none` should be treated as LAN-only and is not recommended for WAN.
- For WAN exposure, use a TLS reverse proxy and enable authentication.
- `App__Security__EnforceHttps=true` should only be enabled when ASP.NET Core terminates TLS directly (not behind a TLS offloading reverse proxy).

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
