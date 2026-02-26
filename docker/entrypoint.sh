#!/bin/sh
set -eu

PUID="${PUID:-10001}"
PGID="${PGID:-10001}"
DATA_DIR="${App__DataDir:-/app/data}"

echo "[Feedarr] PUID=${PUID}  PGID=${PGID}  DATA_DIR=${DATA_DIR}"

# --- Remap feedarr user/group to requested PUID/PGID ---
# Pattern standard pour containers NAS-friendly (style LinuxServer.io).
# L'utilisateur "feedarr" est redéfini sur le PUID:PGID demandé, puis
# gosu bascule vers cet utilisateur nommé — plus propre qu'un UID anonyme.
# -o : accepte les UID/GID non-uniques (safe si un uid système existant coincide).
if [ "$(id -u feedarr)" != "$PUID" ]; then
    usermod -o -u "$PUID" feedarr
fi
if [ "$(getent group feedarr | cut -d: -f3)" != "$PGID" ]; then
    groupmod -o -g "$PGID" feedarr
fi

# --- Création des dossiers attendus par l'app ---
mkdir -p \
  "$DATA_DIR" \
  "$DATA_DIR/keys" \
  "$DATA_DIR/logs" \
  "$DATA_DIR/posters" \
  "$DATA_DIR/backups"

# --- Correction de l'ownership sur le bind mount ---
# Sur NAS, les volumes arrivent souvent avec un owner/ACL incompatible.
chown -R feedarr:feedarr "$DATA_DIR" 2>/dev/null || true
chmod -R u+rwX,g+rX     "$DATA_DIR" 2>/dev/null || true

echo "[Feedarr] Running as: uid=$(id -u feedarr) gid=$(id -g feedarr)"

# --- Drop privileges et démarrage de l'API ---
exec gosu feedarr dotnet /app/Feedarr.Api.dll
