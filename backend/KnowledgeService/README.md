# KnowledgeService

A Python microservice for the IntelliLect platform. Its eventual purpose is to
ingest classroom files (PDF, `.docx`, `.pptx`), extract and OCR their text, chunk
it, embed the chunks with the Gemini API, store them in PostgreSQL + `pgvector`,
and serve retrieval.

**This build is the foundation only.** Extraction, OCR, chunking, real embedding
of files, and retrieval are intentionally **not** implemented — the code leaves
clearly-marked ports and placeholders for them.

## What works today

- Clean-architecture skeleton with strict dependency inversion.
- `documents` and `chunks` tables (with a `pgvector` embedding column) created by
  the first Alembic migration.
- A Gemini embedding adapter behind an `EmbeddingProvider` port.
- `GET /health` — verifies `SELECT 1` against the database.
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
| **Infrastructure** | `app/infrastructure` | Implements the ports. `config/` (pydantic-settings), `persistence/` (SQLAlchemy ORM models, async engine/session, repository impls, Alembic env), `embeddings/` (`GeminiEmbeddingProvider`). No business logic. | application, domain, frameworks/SDKs |
| **API** | `app/api` | FastAPI app factory, DI wiring (`dependencies.py`, the composition root), routers. Depends only on application **ports**, resolved via FastAPI dependencies. | application ports + infrastructure (only in `dependencies.py`) |

Key rules honored here:

- The domain entities are plain dataclasses, **separate** from the SQLAlchemy ORM
  models. The persistence layer maps between them.
- The embedding **vector lives on the ORM model** (`chunks.embedding`), never on
  the `Chunk` domain entity.
- The API layer names concrete infrastructure classes in exactly one place —
  `app/api/dependencies.py` — and everything else depends on the port
  abstractions.

## Configuration

Configuration is read from environment variables via `pydantic-settings`
(`app/infrastructure/config/settings.py`). Copy `.env.example` to `.env` and
adjust:

| Variable | Default | Purpose |
|----------|---------|---------|
| `DATABASE_URL` | — | Async SQLAlchemy URL (`postgresql+asyncpg://…`). |
| `GEMINI_API_KEY` | `""` | Gemini API key for the embedding adapter. |
| `EMBEDDING_MODEL` | `gemini-embedding-001` | Gemini embedding model. |
| `EMBEDDING_DIM` | `768` | Truncated output dimensionality; also the `pgvector` column dimension. |
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
# Set a real GEMINI_API_KEY if you intend to call the embedding adapter.

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
# {"status":"ok","db":"ok"}

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
unreachable-DB cases by patching the session factory, so no live database is
required.

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
- Gemini's `google-genai` SDK is synchronous; the adapter offloads calls to a
  thread (`asyncio.to_thread`) and **L2-normalizes** each vector because a
  truncated `output_dimensionality` is not returned normalized.
- The `chunks.embedding` column is nullable — rows can exist before embeddings are
  computed once the processing pipeline is built.
- Target platform is `linux/arm64` (Apple Silicon); the base and DB images are
  multi-arch, so amd64 also works.
