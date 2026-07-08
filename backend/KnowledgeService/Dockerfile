# Target: linux/arm64 (Apple Silicon) — the base image is multi-arch, so this
# also builds on amd64.
FROM python:3.12-slim

ENV PYTHONUNBUFFERED=1 \
    PYTHONDONTWRITEBYTECODE=1 \
    PIP_NO_CACHE_DIR=1

# Build deps for compiling asyncpg / native wheels. Kept minimal.
# NOTE: OCR/PDF tooling (tesseract-ocr, poppler-utils) will be added here later
# when extraction/OCR lands. Not needed for the foundation.
RUN apt-get update \
    && apt-get install -y --no-install-recommends build-essential \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app

# Install dependencies first for better layer caching.
COPY pyproject.toml README.md ./
COPY app ./app
RUN pip install --upgrade pip && pip install .

# App source + migration assets.
COPY alembic ./alembic
COPY alembic.ini ./alembic.ini
COPY entrypoint.sh ./entrypoint.sh
RUN chmod +x ./entrypoint.sh

EXPOSE 8080

# Applies migrations, then starts the API.
ENTRYPOINT ["./entrypoint.sh"]
