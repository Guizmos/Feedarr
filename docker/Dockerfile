# ── stage 1: build React UI ───────────────────────────────────────────────────
# $BUILDPLATFORM = native arch of the build host (amd64 or arm64).
# Running node/npm natively avoids QEMU emulation for the build stages.
FROM --platform=$BUILDPLATFORM node:22-alpine AS web-build
WORKDIR /app

COPY src/Feedarr.Web/feedarr-web/package*.json ./
RUN npm ci

COPY src/Feedarr.Web/feedarr-web/ ./
RUN npm run build

# ── stage 2: build .NET API ───────────────────────────────────────────────────
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:8.0 AS api-build
WORKDIR /src

COPY src/Feedarr.Api/Feedarr.Api.csproj src/Feedarr.Api/
RUN dotnet restore src/Feedarr.Api/Feedarr.Api.csproj

COPY src/Feedarr.Api/ src/Feedarr.Api/
RUN dotnet publish src/Feedarr.Api/Feedarr.Api.csproj -c Release -o /out

# ── stage 3: runtime ($TARGETPLATFORM: amd64 or arm64) ────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080
ARG FEEDARR_VERSION
ENV FEEDARR_VERSION=$FEEDARR_VERSION

RUN apt-get update \
  && apt-get install -y --no-install-recommends gosu ca-certificates wget \
  && rm -rf /var/lib/apt/lists/*

# API binaries
COPY --from=api-build /out ./

# React build → wwwroot (served as static files by ASP.NET Core)
COPY --from=web-build /app/dist ./wwwroot/

RUN groupadd --gid 10001 feedarr \
    && useradd --uid 10001 --gid 10001 --no-create-home --shell /usr/sbin/nologin feedarr \
    && mkdir -p /app/data \
    && chown -R feedarr:feedarr /app

COPY docker/entrypoint.sh /entrypoint.sh
RUN chmod +x /entrypoint.sh

HEALTHCHECK --interval=30s --timeout=5s --start-period=15s --retries=3 \
  CMD wget -qO- http://localhost:8080/health || exit 1

EXPOSE 8080

USER root
ENTRYPOINT ["/entrypoint.sh"]
