#!/usr/bin/env sh
set -e

# Apply transcript-store migrations (S-0) ONLY when a DB is configured. The service
# runs fully offline without one (in-memory transcript store), so skip migrations when
# TRANSCRIPT_DB_URL is empty rather than failing to start.
if [ -n "${TRANSCRIPT_DB_URL}" ]; then
  echo "TRANSCRIPT_DB_URL set — running Alembic migrations..."
  alembic upgrade head
else
  echo "TRANSCRIPT_DB_URL not set — skipping migrations (in-memory transcript store)."
fi

echo "Starting Uvicorn..."
exec uvicorn app.api.main:app --host 0.0.0.0 --port 8080
