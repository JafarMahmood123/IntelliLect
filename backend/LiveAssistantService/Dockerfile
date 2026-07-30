# Target: linux (Ubuntu host, no GPU assumptions). The base image is multi-arch,
# so this also builds on arm64.
FROM python:3.12-slim

ENV PYTHONUNBUFFERED=1 \
    PYTHONDONTWRITEBYTECODE=1 \
    # Flaky/slow links (VPN) drop connections mid-download. The defaults (5 retries,
    # 15s timeout) give up long before a 76 kB/s link finishes a 1.8 MB wheel, and pip
    # then reports the missing metadata as "no matching distribution" — a dependency
    # error for what is actually a network error. Diagnosed exactly that way once.
    PIP_RETRIES=10 \
    PIP_TIMEOUT=120 \
    PIP_DISABLE_PIP_VERSION_CHECK=1

# Build deps for any native wheels (numpy/livekit rtc bindings ship manylinux
# wheels, but build-essential keeps source-only fallbacks working). No ML/system-
# audio packages — this phase pulls in no models and no PortAudio/ALSA.
RUN apt-get update \
    && apt-get install -y --no-install-recommends build-essential \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app

# Install dependencies BEFORE the app source, so editing code does not re-resolve and
# re-download every dependency. The previous order copied `app` first, which put source
# code in the install layer's cache key — a three-line change then cost a full dependency
# resolution (measured: 484s, and on a flaky link it failed outright).
#
# The stub package is what makes the split possible: `pip install .` needs SOMETHING to
# build, and pyproject is the single source of truth for the dependency list, so we build
# a placeholder to pull the dependencies in, then install the real source with --no-deps.
COPY pyproject.toml README.md ./
RUN mkdir -p app && touch app/__init__.py

# The cache mount keeps downloaded wheels across builds, so a retry after a network drop
# resumes instead of starting over — the difference between "eventually succeeds" and
# "fails at 8 minutes, every time" on this link. Needs BuildKit (Compose v2 default).
RUN --mount=type=cache,target=/root/.cache/pip,sharing=locked \
    pip install .

# Real source. From here on a code change rebuilds only these layers, and --no-deps keeps
# it entirely offline: no index lookup, no downloads.
COPY app ./app
RUN --mount=type=cache,target=/root/.cache/pip,sharing=locked \
    pip install --no-deps .

# Migration assets (S-0 transcript store) + helper scripts + launcher.
COPY alembic ./alembic
COPY alembic.ini ./alembic.ini
COPY scripts ./scripts
COPY entrypoint.sh ./entrypoint.sh
RUN chmod +x ./entrypoint.sh

EXPOSE 8080

ENTRYPOINT ["./entrypoint.sh"]
