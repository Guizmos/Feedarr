FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY src/Feedarr.Api/Feedarr.Api.csproj src/Feedarr.Api/
RUN dotnet restore src/Feedarr.Api/Feedarr.Api.csproj

COPY src/Feedarr.Api/ src/Feedarr.Api/
RUN dotnet publish src/Feedarr.Api/Feedarr.Api.csproj -c Release -o /out

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080
ARG FEEDARR_VERSION
ENV FEEDARR_VERSION=$FEEDARR_VERSION

# gosu pour drop privileges proprement
RUN apt-get update \
  && apt-get install -y --no-install-recommends gosu ca-certificates \
  && rm -rf /var/lib/apt/lists/*

COPY --from=build /out ./

RUN groupadd --gid 10001 feedarr \
    && useradd --uid 10001 --gid 10001 --no-create-home --shell /usr/sbin/nologin feedarr \
    && mkdir -p /app/data \
    && chown -R feedarr:feedarr /app

# entrypoint qui prépare le volume puis lance en feedarr
COPY docker/entrypoint.sh /entrypoint.sh
RUN chmod +x /entrypoint.sh

EXPOSE 8080

USER root
ENTRYPOINT ["/entrypoint.sh"]
