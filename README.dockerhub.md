# Feedarr

Feedarr is a smart release dashboard for Torznab, Jackett, and Prowlarr.

🚀 Monolithic image — API and Web UI are now served from a single container (no nginx, no split services).

Docker Image
------------
Official image:

guizmos/feedarr

Tags:
latest → stable release
beta → beta channel
vX.Y.Z → specific version

⚠️ The old images guizmos/feedarr-api and guizmos/feedarr-web are deprecated and should no longer be used.

Source Code

Main project: https://github.com/Guizmos/Feedarr
Backend: https://github.com/Guizmos/Feedarr/tree/main/src/Feedarr.Api
Frontend: https://github.com/Guizmos/Feedarr/tree/main/src/Feedarr.Web/feedarr-web

# Quick Start (Docker Compose)
version: "3.9"

services:
  feedarr:
    image: guizmos/feedarr:latest
    container_name: FEEDARR
    restart: unless-stopped
    environment:
      ASPNETCORE_URLS: http://+:8080
      App__DataDir: /app/data
    volumes:
      - /volume1/Docker/Feedarr/data:/app/data
    ports:
      - "8888:8080"

# Start:

docker compose up -d
Access

Web UI: http://localhost:8888
API: http://localhost:8888/api
Health endpoint: http://localhost:8888/health
Reverse Proxy (Nginx Proxy Manager / Traefik / Caddy)

Feedarr now runs as a single upstream service.

# Point your reverse proxy to:

http://feedarr:8080
or
http://<host-ip>:8888

You no longer need separate rules for /api and /.

# Data Directory

All persistent data is stored in:

/app/data

This includes:

SQLite database
API keys
Posters cache
Backups
Logs

Mount this path to keep your data safe.

Upgrade from older versions (split API/Web)

Stop your old containers

Remove feedarr-api and feedarr-web services from compose

Replace them with the single feedarr service

Keep the same /app/data volume

Start again

No data migration is required.

### Private Docker/LAN integrations and SSRF

Feedarr has **no SSRF exceptions by default**. To connect self-hosted Jackett, Prowlarr, Torznab, Sonarr, Radarr, Overseerr, Jellyseerr or Seer on a private Docker/LAN network, configure the container environment **before the initial setup**.

**Recommended: exact destinations (`TrustedHosts`)**

```yaml
environment:
  App__Security__TrustedHosts__Prowlarr: "prowlarr:9696"
  App__Security__TrustedHosts__Sonarr: "sonarr:8989"
  App__Security__TrustedHosts__Radarr: "radarr:7878"
  App__Security__TrustedHosts__Jackett: "jackett:9117"
  App__Security__TrustedHosts__Seer: "10.50.0.15:5055"
```

Each value requires an exact hostname/IP **and port**. No wildcards or implicit ports are accepted. Labels such as `Prowlarr` are descriptive; add more uniquely named entries as needed.

**Advanced: private networks (`TrustedNetworks`)**

```yaml
environment:
  App__Security__TrustedNetworks__0: "10.50.0.0/24"
  App__Security__TrustedNetworks__1: "192.168.1.0/24"
```

**Warning: network mode is less restrictive.** It authorizes eligible integration traffic to any destination and port in the configured subnet. Prefer exact hosts when possible.

Both modes can be combined under the same `environment` mapping. Both lists are extensible, with no fixed entry-count limit. Networks must be canonical IPv4 RFC1918 CIDRs wholly within `10.0.0.0/8`, `172.16.0.0/12` or `192.168.0.0/16`. Public networks, IPv6 CIDRs and CIDRs with host bits set are rejected at startup. For example, use `10.50.0.0/24`, not `10.50.0.15/24`. **Do not use `172.50.0.0/24` for a private Docker network: it is public address space.**

After changing environment variables, recreate the Feedarr container using `docker compose up -d --force-recreate feedarr` (adapt the service name). A simple container restart does not apply changed environment variables. Services must actually be reachable from Feedarr's container: Docker service names require a shared network; LAN addresses require routing and firewall access. `localhost` points to Feedarr's own container and remains blocked. Invalid configuration prevents startup; check container logs.

Exceptions apply only to these integration clients. Loopback, link-local, cloud metadata and dangerous special destinations stay blocked. DNS answers and new connections are validated; redirects cannot change host or port. TLS certificate verification remains enabled by default. Changing these options requires restarting Feedarr after applying the new container environment.

A `403` with code `ssrf_blocked` is a security refusal, not a discovery failure: manual indexer entry cannot bypass it. A source or application may already have been saved before its CAPS or initial synchronization fails. Refresh the list, correct the environment, recreate the container, then retest the existing entry instead of creating a duplicate.

**Release download limitation:** these exceptions never apply to downloads, posters, metadata or arbitrary URLs. Feedarr proxies HTTP torrent downloads through a strict client. If a release's download URL points to private Jackett/Prowlarr (including a Docker name resolving to a private IP), downloading it through Feedarr remains blocked even when discovery, CAPS and RSS work. There is no automatic browser fallback. Literal private URLs are rejected during URL validation; private DNS answers are rejected before the proxy request. Public HTTP download URLs remain subject to strict checks; magnet links continue to open through the browser/OS handler. Use the provider's own interface for private torrent downloads where available. Adding a trusted host or subnet does not remove this limitation.
