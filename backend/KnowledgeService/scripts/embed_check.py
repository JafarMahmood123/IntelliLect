"""Smoke-test the local embedding provider against host Ollama.

Usage (from the KnowledgeService directory, with a .env present):

    python scripts/embed_check.py

Or inside the running container (recommended, since host.docker.internal and the
model are already reachable there):

    docker compose exec knowledge-service python scripts/embed_check.py

It embeds ["hello world"] via OllamaEmbeddingProvider and prints the returned
vector length — expected to equal EMBEDDING_DIM (1024 by default). Requires host
Ollama to be running with the configured embedding model pulled.
"""

from __future__ import annotations

import asyncio
import sys

from app.infrastructure.config.settings import get_settings
from app.infrastructure.embeddings.ollama_embedding_provider import (
    OllamaEmbeddingError,
    OllamaEmbeddingProvider,
)


async def main() -> int:
    settings = get_settings()
    provider = OllamaEmbeddingProvider(settings)
    print(f"model={settings.embedding_model!r} base_url={settings.ollama_base_url!r}")
    try:
        vectors = await provider.embed_documents(["hello world"])
    except OllamaEmbeddingError as exc:
        print(f"FAILED: {exc}", file=sys.stderr)
        return 1

    length = len(vectors[0])
    print(f"vector length: {length} (expected {settings.embedding_dim})")
    return 0 if length == settings.embedding_dim else 2


if __name__ == "__main__":
    raise SystemExit(asyncio.run(main()))
