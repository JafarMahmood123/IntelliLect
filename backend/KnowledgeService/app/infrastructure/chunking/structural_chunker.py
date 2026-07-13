from __future__ import annotations

from app.application.services.token_counter import TokenCounter
from app.domain.extraction.text_block import TextBlock
from app.infrastructure.chunking._text_splitter import Atom, RecursiveTextSplitter
from app.infrastructure.chunking.base import BaseChunker
from app.infrastructure.config.settings import Settings


class StructuralChunker(BaseChunker):
    """Default chunker — structure only, no model.

    Within each page/slide/section group, blocks are atomized (paragraph -> sentence
    -> word -> hard wrap), packed into <= CHUNK_MAX_TOKENS chunks with
    CHUNK_OVERLAP_TOKENS overlap, and trailing sub-CHUNK_MIN_TOKENS fragments are
    merged back into the previous chunk.
    """

    def __init__(self, settings: Settings, counter: TokenCounter) -> None:
        super().__init__(counter)
        self._min_tokens = settings.chunk_min_tokens
        self._splitter = RecursiveTextSplitter(
            counter=counter,
            max_tokens=settings.chunk_max_tokens,
            overlap_tokens=settings.chunk_overlap_tokens,
        )

    async def _split_group(self, blocks: list[TextBlock]) -> list[list[Atom]]:
        atoms = self._splitter.atomize_blocks(blocks)
        if not atoms:
            return []
        chunks = self._splitter.pack(atoms)
        return self._splitter.merge_small_tail(chunks, self._min_tokens)
