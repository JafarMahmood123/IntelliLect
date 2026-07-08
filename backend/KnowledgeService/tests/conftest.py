import os

# Settings require these env vars at import time. Set safe test defaults BEFORE
# any app module is imported. No real database or Gemini key is needed for the
# tests in this suite.
os.environ.setdefault(
    "DATABASE_URL", "postgresql+asyncpg://postgres:postgres@localhost:5432/testdb"
)
os.environ.setdefault("GEMINI_API_KEY", "test-key")
os.environ.setdefault("INTERNAL_API_SECRET", "test-internal-secret")
os.environ.setdefault("EMBEDDING_DIM", "768")
