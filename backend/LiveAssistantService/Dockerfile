# Target: linux (Ubuntu host, no GPU assumptions). The base image is multi-arch,
# so this also builds on arm64.
FROM python:3.12-slim

ENV PYTHONUNBUFFERED=1 \
    PYTHONDONTWRITEBYTECODE=1 \
    PIP_NO_CACHE_DIR=1

# Build deps for any native wheels (numpy/livekit rtc bindings ship manylinux
# wheels, but build-essential keeps source-only fallbacks working). No ML/system-
# audio packages — this phase pulls in no models and no PortAudio/ALSA.
RUN apt-get update \
    && apt-get install -y --no-install-recommends build-essential \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app

# Install dependencies first for better layer caching.
COPY pyproject.toml README.md ./
COPY app ./app
RUN pip install --upgrade pip && pip install .

# Migration assets (S-0 transcript store) + helper scripts + launcher.
COPY alembic ./alembic
COPY alembic.ini ./alembic.ini
COPY scripts ./scripts
COPY entrypoint.sh ./entrypoint.sh
RUN chmod +x ./entrypoint.sh

EXPOSE 8080

ENTRYPOINT ["./entrypoint.sh"]
