# Target: linux (Ubuntu host, no GPU assumptions). The base image is multi-arch,
# so this also builds on arm64.
FROM python:3.12-slim

ENV PYTHONUNBUFFERED=1 \
    PYTHONDONTWRITEBYTECODE=1 \
    PIP_NO_CACHE_DIR=1

# Build deps for compiling asyncpg / native wheels (build-essential), plus the
# Tesseract OCR engine and its English language data (Phase 3, English only).
RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        build-essential \
        tesseract-ocr \
        tesseract-ocr-eng \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app

# Install dependencies first for better layer caching.
COPY pyproject.toml README.md ./
COPY app ./app
RUN pip install --upgrade pip && pip install .

# App source + migration assets + helper scripts.
COPY alembic ./alembic
COPY alembic.ini ./alembic.ini
COPY scripts ./scripts
COPY entrypoint.sh ./entrypoint.sh
RUN chmod +x ./entrypoint.sh

EXPOSE 8080

# Applies migrations, then starts the API.
ENTRYPOINT ["./entrypoint.sh"]
