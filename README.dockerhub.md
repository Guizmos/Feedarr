# Feedarr

Feedarr is a smart release dashboard for Torznab, Jackett, and Prowlarr.

This Docker Hub repository is used as the stack overview page.

## Stack Images

- API image: [`guizmos/feedarr-api`](https://hub.docker.com/r/guizmos/feedarr-api)
- Web image: [`guizmos/feedarr-web`](https://hub.docker.com/r/guizmos/feedarr-web)

## Source Code

- Main project: [`Guizmos/Feedarr`](https://github.com/Guizmos/Feedarr)
- API source path: [`src/Feedarr.Api`](https://github.com/Guizmos/Feedarr/tree/main/src/Feedarr.Api)
- Web source path: [`src/Feedarr.Web/feedarr-web`](https://github.com/Guizmos/Feedarr/tree/main/src/Feedarr.Web/feedarr-web)

## Quick Start (Docker Compose)

```yaml
version: "3.9"

services:
  api:
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

- Web UI: `http://localhost:8888`
- API: `http://localhost:9999`
