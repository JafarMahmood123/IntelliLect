#!/usr/bin/env sh
set -e

# No database / migrations in this service — just launch the API server.
echo "Starting Uvicorn..."
exec uvicorn app.api.main:app --host 0.0.0.0 --port 8080
