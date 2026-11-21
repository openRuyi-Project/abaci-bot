#!/usr/bin/env bash
set -euo pipefail

GIT_COMMIT=$(git rev-parse --short=12 HEAD)
GIT_DIRTY=$([ -n "$(git status --porcelain)" ] && echo "true" || echo "false")

exec docker build \
  --build-arg "GIT_COMMIT=$GIT_COMMIT" \
  --build-arg "GIT_DIRTY=$GIT_DIRTY" \
  "$@" .
