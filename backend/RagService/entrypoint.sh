#!/usr/bin/env sh
set -e

# Apply database migrations, then launch the API server.
# The DB is guaranteed healthy first via the compose depends_on/healthcheck.
echo "Running Alembic migrations..."
alembic upgrade head

echo "Starting Uvicorn..."
exec uvicorn app.api.main:app --host 0.0.0.0 --port 8080
