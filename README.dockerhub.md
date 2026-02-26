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