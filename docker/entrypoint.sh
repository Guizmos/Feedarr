#!/bin/sh
set -eu

DATA_DIR="${App__DataDir:-/app/data}"

# Valeurs stables par défaut (celles de l'image)
PUID="${PUID:-10001}"
PGID="${PGID:-10001}"

# Crée les dossiers attendus par l'app
mkdir -p \
  "$DATA_DIR" \
  "$DATA_DIR/keys" \
  "$DATA_DIR/logs" \
  "$DATA_DIR/posters" \
  "$DATA_DIR/backups"

# Sur NAS, les bind mounts arrivent souvent avec un owner/acl incompatible
# => on corrige (best effort)
chown -R "$PUID:$PGID" "$DATA_DIR" 2>/dev/null || true
chmod -R u+rwX,g+rwX "$DATA_DIR" 2>/dev/null || true

# Drop privileges et démarre l'API
exec gosu feedarr:feedarr dotnet /app/Feedarr.Api.dll
