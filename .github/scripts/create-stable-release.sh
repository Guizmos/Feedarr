#!/usr/bin/env bash

set -euo pipefail

BUMP="${1:-patch}"
PROJECT_FILE="src/Feedarr.Api/Feedarr.Api.csproj"

write_step() {
  echo "[stable-release] $1"
}

current_branch="$(git rev-parse --abbrev-ref HEAD)"
write_step "Branche courante: ${current_branch}"

if [[ "${current_branch}" != "main" ]]; then
  echo "La release stable ne peut etre creee que depuis 'main'." >&2
  exit 1
fi

head_tag="$(git tag --points-at HEAD --list 'v*' --sort=-v:refname | grep -E '^v[0-9]+\.[0-9]+\.[0-9]+$' | head -n 1 || true)"
if [[ -n "${head_tag}" ]]; then
  write_step "HEAD porte deja le tag ${head_tag}. Rien a faire."
  exit 0
fi

latest_tag="$(git tag --merged HEAD --list 'v*' --sort=-v:refname | grep -E '^v[0-9]+\.[0-9]+\.[0-9]+$' | head -n 1 || true)"
if [[ -z "${latest_tag}" ]]; then
  latest_tag="v0.0.0"
fi

version="${latest_tag#v}"
IFS='.' read -r major minor patch <<< "${version}"
major="${major:-0}"
minor="${minor:-0}"
patch="${patch:-0}"

case "${BUMP}" in
  patch) patch=$((patch + 1)) ;;
  minor) minor=$((minor + 1)); patch=0 ;;
  major) major=$((major + 1)); minor=0; patch=0 ;;
  *)
    echo "Bump non supporte: ${BUMP}" >&2
    exit 1
    ;;
esac

tag="v${major}.${minor}.${patch}"
version="${tag#v}"

write_step "Dernier tag stable sur main: ${latest_tag}"
write_step "Nouveau tag stable: ${tag}"

if git tag --list "${tag}" | grep -qx "${tag}"; then
  echo "Le tag ${tag} existe deja." >&2
  exit 1
fi

if ! command -v gh >/dev/null 2>&1; then
  echo "GitHub CLI (gh) non disponible sur le runner." >&2
  exit 1
fi

: "${GITHUB_REPOSITORY:?Variable GITHUB_REPOSITORY absente.}"
: "${GITHUB_TOKEN:?Variable GITHUB_TOKEN absente.}"

write_step "Mise a jour de la version source: ${version}"
bash ./.github/scripts/set-app-version.sh "${version}" "${PROJECT_FILE}"

if ! git diff --quiet -- "${PROJECT_FILE}"; then
  git add "${PROJECT_FILE}"
  git commit -m "chore: sync app version ${version} [skip ci]"
  git push origin HEAD:main
  write_step "Version source poussee sur main."
else
  write_step "Version source deja a jour."
fi

git tag -a "${tag}" -m "Release ${tag}"
git push origin "refs/tags/${tag}"
GH_TOKEN="${GITHUB_TOKEN}" gh release create "${tag}" --repo "${GITHUB_REPOSITORY}" --generate-notes --title "${tag}"

write_step "Release stable publiee: ${tag}"
