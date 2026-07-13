from __future__ import annotations

import math

from app.application.ports.embedding_provider import EmbeddingProvider
from app.application.services.token_counter import TokenCounter
from app.domain.extraction.text_block import TextBlock
from app.infrastructure.chunking._text_splitter import (
    Atom,
    RecursiveTextSplitter,
    split_sentences,
)
from app.infrastructure.chunking.base import BaseChunker
from app.infrastructure.config.settings import Settings


class SemanticChunker(BaseChunker):
    """Chunker that places breakpoints where the topic shifts.

    Within each page/slide/section group it embeds the sentences (its ONLY model
    use), measures cosine distance between consecutive sentences, and starts a new
    chunk wherever the distance exceeds the SEMANTIC_BREAKPOINT_PERCENTILE. Spans are
    emitted as chunks; a span larger than CHUNK_MAX_TOKENS falls back to structural
    splitting. Boundaries are never crossed. Requires a live EmbeddingProvider — in
    this phase it is exercised only against a deterministic fake.
    """

    def __init__(
        self,
        settings: Settings,
        embedding_provider: EmbeddingProvider,
        counter: TokenCounter,
    ) -> None:
        super().__init__(counter)
        self._embedder = embedding_provider
        self._percentile = settings.semantic_breakpoint_percentile
        self._max_tokens = settings.chunk_max_tokens
        self._splitter = RecursiveTextSplitter(
            counter=counter,
            max_tokens=settings.chunk_max_tokens,
            overlap_tokens=settings.chunk_overlap_tokens,
        )

    async def _split_group(self, blocks: list[TextBlock]) -> list[list[Atom]]:
        sentences: list[Atom] = []
        for block in blocks:
            text = block.text.strip()
            if not text:
                continue
            for sentence in split_sentences(text):
                sentences.append(Atom(text=sentence, source=block.source))

        if not sentences:
            return []
        if len(sentences) == 1:
            return self._emit_span(sentences)

        vectors = await self._embedder.embed_documents([a.text for a in sentences])
        distances = [
            1.0 - _cosine_similarity(vectors[i], vectors[i + 1])
            for i in range(len(vectors) - 1)
        ]
        threshold = _percentile(distances, self._percentile)

        chunks: list[list[Atom]] = []
        span: list[Atom] = [sentences[0]]
        for i, distance in enumerate(distances):
            if distance > threshold:
                chunks.extend(self._emit_span(span))
                span = [sentences[i + 1]]
            else:
                span.append(sentences[i + 1])
        chunks.extend(self._emit_span(span))
        return chunks

    def _emit_span(self, span: list[Atom]) -> list[list[Atom]]:
        """A span is one chunk if it fits; otherwise fall back to structural split."""
        if not span:
            return []
        if self._splitter.count_atoms(span) <= self._max_tokens:
            return [span]
        atoms: list[Atom] = []
        for sentence in span:
            for piece in self._splitter.atomize(sentence.text):
                atoms.append(Atom(text=piece, source=sentence.source))
        return self._splitter.pack(atoms)


def _cosine_similarity(a: list[float], b: list[float]) -> float:
    dot = sum(x * y for x, y in zip(a, b))
    norm_a = math.sqrt(sum(x * x for x in a))
    norm_b = math.sqrt(sum(y * y for y in b))
    if norm_a == 0.0 or norm_b == 0.0:
        return 0.0
    return dot / (norm_a * norm_b)


def _percentile(values: list[float], percentile: float) -> float:
    """Linear-interpolated percentile (0-100). Empty -> 0.0."""
    if not values:
        return 0.0
    ordered = sorted(values)
    if len(ordered) == 1:
        return ordered[0]
    rank = (len(ordered) - 1) * (percentile / 100.0)
    low = math.floor(rank)
    high = math.ceil(rank)
    if low == high:
        return ordered[int(rank)]
    return ordered[low] + (ordered[high] - ordered[low]) * (rank - low)
