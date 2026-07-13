from __future__ import annotations

from app.application.ports.chunker import Chunker
from app.application.ports.embedding_provider import EmbeddingProvider
from app.application.services.token_counter import HeuristicTokenCounter, TokenCounter
from app.infrastructure.chunking.semantic_chunker import SemanticChunker
from app.infrastructure.chunking.structural_chunker import StructuralChunker
from app.infrastructure.config.settings import Settings

STRUCTURAL = "structural"
SEMANTIC = "semantic"


def create_chunker(
    settings: Settings,
    embedding_provider: EmbeddingProvider,
    token_counter: TokenCounter | None = None,
) -> Chunker:
    """Pick a Chunker from CHUNKING_STRATEGY (default structural).

    `embedding_provider` is only used by the semantic strategy; the structural
    default ignores it and stays fully offline.
    """
    counter = token_counter or HeuristicTokenCounter()
    strategy = (settings.chunking_strategy or STRUCTURAL).strip().lower()
    if strategy == SEMANTIC:
        return SemanticChunker(settings, embedding_provider, counter)
    return StructuralChunker(settings, counter)
