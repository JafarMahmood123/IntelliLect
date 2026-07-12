# KnowledgeService

A Python microservice for the IntelliLect platform. Its eventual purpose is to
ingest classroom files (PDF, `.docx`, `.pptx`), extract and OCR their text, chunk
it, embed the chunks with a **local embedding model (via host Ollama)**, store them
in PostgreSQL + `pgvector`, and serve retrieval.

**This build is the foundation only.** Extraction, OCR, chunking, real embedding
of files, and retrieval are intentionally **not** implemented — the code leaves
clearly-marked ports and placeholders for them.

> **No model weights in the container.** There is no `torch`,
> `transformers`, or `sentence-transformers` dependency. All embedding inference
> is HTTP calls to a host-side Ollama server.

## What works today

- Clean-architecture skeleton with strict dependency inversion.
- `documents` and `chunks` tables (with a `pgvector` embedding column) created by
  the first Alembic migration.
- A **local Ollama** embedding adapter behind an `EmbeddingProvider` port.
- `GET /health` — verifies `SELECT 1` against the database (fatal) and probes host
  Ollama (informational): `{"status","db","ollama"}`.
- `POST /api/internal/documents/ingest` — upserts a **Pending** `Document` row
  (idempotent on `fileId`) and returns `202 Accepted`. It does **not** process the
  file.
- `DELETE /api/internal/documents/{fileId}` — deletes the document (its chunks
  cascade) and returns `204`.

## Architecture (Clean Architecture)

Dependencies point **inward**. Each layer may only depend on the ones listed
below it.

| Layer | Path | Responsibility | May import |
|-------|------|----------------|------------|
| **Domain** | `app/domain` | Pure business objects: `Document`, `Chunk` entities (dataclasses) and enums (`DocumentStatus`, `ChunkSource`). Zero framework/ORM/SDK imports. | stdlib only |
| **Application** | `app/application` | Ports (ABCs): `EmbeddingProvider`, `DocumentRepository`, `ChunkRepository`. Request/response DTOs (pydantic). `services/` is a placeholder for future use cases. | domain |
| **Infrastructure** | `app/infrastructure` | Implements the ports. `config/` (pydantic-settings), `persistence/` (SQLAlchemy ORM models, async engine/session, repository impls, Alembic env), `embeddings/` (`OllamaEmbeddingProvider`). No business logic. | application, domain, frameworks/SDKs |
| **API** | `app/api` | FastAPI app factory, DI wiring (`dependencies.py`, the composition root), routers. Depends only on application **ports**, resolved via FastAPI dependencies. | application ports + infrastructure (only in `dependencies.py`) |

Key rules honored here:

- The domain entities are plain dataclasses, **separate** from the SQLAlchemy ORM
  models. The persistence layer maps between them.
- The embedding **vector lives on the ORM model** (`chunks.embedding`), never on
  the `Chunk` domain entity.
- The API layer names concrete infrastructure classes in exactly one place —
  `app/api/dependencies.py` — and everything else depends on the port
  abstractions.

## Embeddings (local Ollama)

`OllamaEmbeddingProvider` talks to a host-side Ollama server over HTTP:

- `embed_documents(texts)` → `POST {OLLAMA_BASE_URL}/api/embed` with
  `{"model": EMBEDDING_MODEL, "input": texts}`, batched. Documents are embedded
  **raw**.
- `embed_query(text)` prepends a configurable retrieval instruction to the
  **query only** (asymmetric embedding), then embeds it.
- Every returned vector is **L2-normalized** (the pgvector index uses cosine).
- If `OLLAMA_AUTH_TOKEN` is set it is sent as `Authorization: Bearer <token>`;
  otherwise no auth header is sent.
- Unreachable server or a missing model raises a clear `OllamaEmbeddingError`
  telling you to run `ollama pull …` or check `OLLAMA_BASE_URL`.

### Host Ollama requirement

Host Ollama must be **running and bound to `0.0.0.0:11434`** with the embedding
model pulled:

```bash
# Bind to all interfaces so the container can reach it via host.docker.internal.
OLLAMA_HOST=0.0.0.0 ollama serve
ollama pull qwen3-embedding
```

The container reaches the host through the `extra_hosts:
host.docker.internal:host-gateway` mapping in `docker-compose.unit.yml` (required
on Linux; Docker Desktop provides `host.docker.internal` regardless).

> **Cold start.** The **first** embedding call after Ollama (re)starts loads the
> model into memory and can take longer than `EMBEDDING_TIMEOUT_SECONDS` on a
> CPU-only host (observed ~90 s for `qwen3-embedding`). Warm the model once
> (`curl -X POST localhost:11434/api/embed -d '{"model":"qwen3-embedding","input":["warmup"]}'`)
> or raise `EMBEDDING_TIMEOUT_SECONDS` if you hit a timeout on the first request.

## Configuration

Configuration is read from environment variables via `pydantic-settings`
(`app/infrastructure/config/settings.py`). Copy `.env.example` to `.env` and
adjust:

| Variable | Default | Purpose |
|----------|---------|---------|
| `DATABASE_URL` | — | Async SQLAlchemy URL (`postgresql+asyncpg://…`). |
| `OLLAMA_BASE_URL` | `http://host.docker.internal:11434` | Host Ollama base URL. |
| `OLLAMA_AUTH_TOKEN` | `""` | Optional bearer token; sent only if set. |
| `EMBEDDING_MODEL` | `qwen3-embedding` | Ollama embedding model name. |
| `EMBEDDING_DIM` | `1024` | Embedding dimensionality; also the `pgvector` column dimension. |
| `EMBEDDING_TIMEOUT_SECONDS` | `60` | Per-request timeout for embedding calls. |
| `RETRIEVAL_INSTRUCTION` | see settings | Instruction prepended to queries; must contain `{query}`. |
| `INTERNAL_API_SECRET` | `""` | Shared secret for `/api/internal/*` routes (header `X-Internal-Secret`). |
| `S3_*` | `""` | Placeholders for object storage. **Unused for now.** |

> `EMBEDDING_DIM` is read by both the ORM models and the Alembic migration, so the
> schema and the app always agree. Changing it after the first migration requires
> a new migration that alters the vector column.

## Run via docker-compose

The service is wired into the platform compose file
(`backend/docker-compose.yml`) as an `include`. From `backend/`:

```bash
# Create the service env file first (compose reads it via env_file).
cp KnowledgeService/.env.example KnowledgeService/.env
# Ensure host Ollama is running (0.0.0.0:11434) with qwen3-embedding pulled.

docker-compose up --build knowledge-service knowledge-db
```

- `knowledge-db` runs `pgvector/pgvector:pg16` with a named volume and a
  healthcheck.
- `knowledge-service` waits for the DB to be healthy (`service_healthy`), then its
  entrypoint applies Alembic migrations and starts Uvicorn.
- The API is exposed on **`http://localhost:8083`** (container port `8080`).

Verify:

```bash
curl http://localhost:8083/health
# {"status":"ok","db":"ok","ollama":"reachable"}

curl -X POST http://localhost:8083/api/internal/documents/ingest \
  -H "Content-Type: application/json" \
  -H "X-Internal-Secret: changeme-internal-secret" \
  -d '{
        "fileId": "11111111-1111-1111-1111-111111111111",
        "classroomId": "22222222-2222-2222-2222-222222222222",
        "s3Key": "classrooms/22222222/notes.pdf",
        "fileName": "notes.pdf",
        "contentType": "application/pdf"
      }'
# 202 Accepted -> a Pending row now exists in `documents`.
```

Check the embedding provider end-to-end (prints the vector length, expected 1024):

```bash
docker compose exec knowledge-service python scripts/embed_check.py
# model='qwen3-embedding' base_url='http://host.docker.internal:11434'
# vector length: 1024 (expected 1024)
```

## Alembic migrations

Migrations are configured for async (`alembic/env.py`) and read `DATABASE_URL`
from `Settings`, so no URL is hard-coded in `alembic.ini`.

Inside the running container they are applied automatically on startup. To run
them manually (e.g. locally, with `DATABASE_URL` pointing at a reachable DB):

```bash
cd backend/KnowledgeService
pip install -e ".[dev]"

alembic upgrade head        # apply all migrations
alembic downgrade -1        # roll back the last one
alembic revision -m "msg"   # scaffold a new migration
```

The first migration (`0001_initial`):

1. `CREATE EXTENSION IF NOT EXISTS vector;`
2. creates `documents` and `chunks`;
3. creates an **HNSW** index on `chunks.embedding` using `vector_cosine_ops`;
4. creates btree indexes on `chunks.classroom_id` and `documents.file_id`.

## Tests

```bash
cd backend/KnowledgeService
pip install -e ".[dev]"
pytest
```

`tests/test_health.py` exercises `GET /health` for both the reachable and
unreachable-DB cases by patching the session factory and the Ollama probe, so no
live database or Ollama server is required.

## Assumptions

- **Ingest is idempotent on `fileId`** via a Postgres `INSERT … ON CONFLICT`
  upsert, so a retried ingest updates the existing row instead of erroring.
- **Internal auth** uses a shared-secret header (`X-Internal-Secret`) and fails
  closed if `INTERNAL_API_SECRET` is unset — matching the internal service-to-
  service call model of the other IntelliLect services rather than JWT.
- The service is exposed on host port **8083** to avoid clashing with the existing
  services; adjust in `docker-compose.unit.yml` if needed.
- DTOs accept **camelCase** field names (`fileId`, `classroomId`, …) to match the
  .NET callers while keeping snake_case in Python.
- Embeddings are **L2-normalized** in the adapter because Ollama does not normalize
  its output and the cosine index assumes unit vectors.
- The **Ollama check in `/health` is non-fatal** — the service reports `200` (with
  `"ollama":"unreachable"`) when only Ollama is down, since it isn't needed to
  register documents.
- The `chunks.embedding` column is nullable — rows can exist before embeddings are
  computed once the processing pipeline is built.
- Target platform is **linux** (Ubuntu host, no GPU assumptions); the base and DB
  images are multi-arch.
