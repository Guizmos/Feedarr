#!/usr/bin/env bash

set -euo pipefail

VERSION="${1:?Usage: set-app-version.sh <version> [project-file]}"
PROJECT_FILE="${2:-src/Feedarr.Api/Feedarr.Api.csproj}"

if [[ ! -f "${PROJECT_FILE}" ]]; then
  echo "Project file not found: ${PROJECT_FILE}" >&2
  exit 1
fi

if [[ ! "${VERSION}" =~ ^([0-9]+)\.([0-9]+)\.([0-9]+)(-[0-9A-Za-z.-]+)?$ ]]; then
  echo "Unsupported version format: ${VERSION}" >&2
  exit 1
fi

MAJOR="${BASH_REMATCH[1]}"
MINOR="${BASH_REMATCH[2]}"
PATCH="${BASH_REMATCH[3]}"
ASSEMBLY_VERSION="${MAJOR}.${MINOR}.${PATCH}.0"

sed -i -E \
  -e "s#<Version>[^<]+</Version>#<Version>${VERSION}</Version>#" \
  -e "s#<AssemblyVersion>[^<]+</AssemblyVersion>#<AssemblyVersion>${ASSEMBLY_VERSION}</AssemblyVersion>#" \
  -e "s#<FileVersion>[^<]+</FileVersion>#<FileVersion>${ASSEMBLY_VERSION}</FileVersion>#" \
  -e "s#<InformationalVersion>[^<]+</InformationalVersion>#<InformationalVersion>${VERSION}</InformationalVersion>#" \
  "${PROJECT_FILE}"

echo "Updated ${PROJECT_FILE} to ${VERSION}"
