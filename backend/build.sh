#!/usr/bin/env bash
#
# Sequential builder for the IntelliLect stack.
#
# Why this exists: the Docker engine here runs inside a memory-constrained VM
# (~3.66 GiB). `docker compose up --build` builds every service in parallel, so
# multiple `dotnet restore` processes (1-2 GiB each) plus the tesseract apt-get
# contend for that small pool and the .NET runtime can crash with SIGSEGV (139).
# Building one service at a time keeps peak memory to a single restore.
#
# Usage:
#   ./build.sh          # build every service sequentially, then `up -d`
#   ./build.sh --no-up  # build only, don't start the stack
set -euo pipefail

cd "$(dirname "$0")"

# Buildable services, in dependency-friendly order. Infra (mq, s3, gateway) are
# prebuilt images and need no build step.
SERVICES=(
  user-service
  email-service
  classroom-service
  streaming-service
  rag-service
  live-assistant-service
)

START_STACK=1
[ "${1:-}" = "--no-up" ] && START_STACK=0

for svc in "${SERVICES[@]}"; do
  echo "==================== building ${svc} ===================="
  docker compose build "${svc}"
done

if [ "${START_STACK}" -eq 1 ]; then
  echo "==================== starting stack (up -d) ===================="
  docker compose up -d
fi

echo "Done."
