"""Offline fixtures for the chunking tests.

No files, no models: ExtractionResults are built from plain TextBlocks, and the
semantic tier is driven by a deterministic FakeEmbeddingProvider that maps topic
keywords to fixed vectors so cosine distances (and thus breakpoints) are known.
"""

from __future__ import annotations

from app.application.ports.embedding_provider import EmbeddingProvider
from app.domain.extraction.extraction_result import ExtractionResult
from app.domain.extraction.text_block import TextBlock, TextBlockKind, TextBlockSource
from app.infrastructure.config.settings import Settings


def text_block(
    order: int,
    text: str,
    *,
    page: int | None = None,
    slide: int | None = None,
    section: str | None = None,
    source: TextBlockSource = TextBlockSource.NATIVE,
    kind: TextBlockKind = TextBlockKind.PARAGRAPH,
) -> TextBlock:
    return TextBlock(
        order=order,
        text=text,
        kind=kind,
        page=page,
        slide=slide,
        section=section,
        source=source,
    )


def result(source_format: str, blocks: list[TextBlock], **kwargs) -> ExtractionResult:
    return ExtractionResult(source_format=source_format, blocks=blocks, **kwargs)


def settings_for(**overrides) -> Settings:
    """Settings with chunking overrides. DATABASE_URL comes from the test env."""
    return Settings(**overrides)


class FakeEmbeddingProvider(EmbeddingProvider):
    """Deterministic embedder: returns a fixed vector per topic keyword.

    A sentence gets the vector of the first keyword it contains (case-insensitive),
    else `default`. Distinct keyword vectors that are orthogonal produce cosine
    distance 1 across a topic shift and 0 within a topic — predictable breakpoints.
    """

    def __init__(
        self,
        topic_vectors: dict[str, list[float]],
        default: list[float] | None = None,
    ) -> None:
        self._topics = {kw.lower(): list(vec) for kw, vec in topic_vectors.items()}
        self._default = list(default) if default is not None else [0.0, 0.0, 0.0, 1.0]
        self.embed_documents_calls = 0
        self.embed_query_calls = 0

    async def embed_documents(self, texts: list[str]) -> list[list[float]]:
        self.embed_documents_calls += 1
        return [self._vector_for(text) for text in texts]

    async def embed_query(self, text: str) -> list[float]:
        self.embed_query_calls += 1
        return self._vector_for(text)

    def _vector_for(self, text: str) -> list[float]:
        lowered = text.lower()
        for keyword, vector in self._topics.items():
            if keyword in lowered:
                return list(vector)
        return list(self._default)
