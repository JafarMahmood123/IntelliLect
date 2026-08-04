import os

# Settings require DATABASE_URL at import time. Set safe test defaults BEFORE any
# app module is imported. No real database or Ollama server is needed for this
# suite — the health test patches both probes.
os.environ.setdefault(
    "DATABASE_URL", "postgresql+asyncpg://postgres:postgres@localhost:5432/testdb"
)
os.environ.setdefault("INTERNAL_API_SECRET", "test-internal-secret")
os.environ.setdefault("OLLAMA_BASE_URL", "http://localhost:11434")
os.environ.setdefault("EMBEDDING_DIM", "1024")
