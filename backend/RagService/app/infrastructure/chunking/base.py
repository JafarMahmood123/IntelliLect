from __future__ import annotations

import logging
from abc import abstractmethod
from dataclasses import dataclass, field
from typing import Any
from uuid import UUID

from app.application.ports.chunker import Chunker
from app.application.services.token_counter import TokenCounter
from app.domain.entities.chunk import Chunk
from app.domain.enums.chunk_source import ChunkSource
from app.domain.extraction.extraction_result import ExtractionResult
from app.domain.extraction.text_block import TextBlock, TextBlockSource
from app.infrastructure.chunking._text_splitter import Atom

logger = logging.getLogger("knowledge.chunking")


@dataclass
class _Group:
    """A run of blocks sharing one natural boundary (a page, slide, or section)."""

    location: dict[str, Any]  # {"page": n} | {"slide": n} | {"section": "..."}
    blocks: list[TextBlock] = field(default_factory=list)


class BaseChunker(Chunker):
    """Shared machinery: boundary grouping, Chunk assembly, source tagging.

    Subclasses implement `_split_group` (how a single boundary group becomes chunks).
    The base guarantees chunks never cross a boundary, indexes them globally from 0,
    and attaches location + source metadata.
    """

    def __init__(self, counter: TokenCounter) -> None:
        self._counter = counter

    async def chunk(
        self,
        result: ExtractionResult,
        document_id: UUID,
        classroom_id: UUID,
    ) -> list[Chunk]:
        chunks: list[Chunk] = []
        index = 0
        for group in self._group_blocks(result):
            for atoms in await self._split_group(group.blocks):
                text = " ".join(atom.text for atom in atoms).strip()
                if not text:
                    continue
                chunks.append(
                    Chunk(
                        document_id=document_id,
                        classroom_id=classroom_id,
                        chunk_index=index,
                        text=text,
                        token_count=self._counter.count(text),
                        metadata=dict(group.location),
                        source=_chunk_source(atoms),
                    )
                )
                index += 1
        logger.info(
            "Chunked %s into %d chunk(s) via %s.",
            result.source_format,
            len(chunks),
            type(self).__name__,
        )
        return chunks

    @abstractmethod
    async def _split_group(self, blocks: list[TextBlock]) -> list[list[Atom]]:
        """Turn one boundary group's blocks into a list of chunk atom-lists."""
        raise NotImplementedError

    def _group_blocks(self, result: ExtractionResult) -> list[_Group]:
        """Bucket non-empty blocks by their boundary value, in first-appearance order.

        Grouping by *value* (not contiguous runs) reunites native blocks with the
        Phase 3 OCR blocks appended after them for the same page/slide.
        """
        fmt = result.source_format
        groups: list[_Group] = []
        by_key: dict[tuple[str, object], _Group] = {}
        for block in sorted(result.blocks, key=lambda b: b.order):
            if not block.text.strip():
                continue
            key = self._boundary_key(fmt, block)
            group = by_key.get(key)
            if group is None:
                group = _Group(location=_location_of(block))
                by_key[key] = group
                groups.append(group)
            group.blocks.append(block)
        return groups

    @staticmethod
    def _boundary_key(fmt: str, block: TextBlock) -> tuple[str, object]:
        if fmt == "pdf":
            return ("page", block.page)
        if fmt == "pptx":
            return ("slide", block.slide)
        if fmt == "docx":
            return ("section", block.section)
        return ("document", None)


def _location_of(block: TextBlock) -> dict[str, Any]:
    location: dict[str, Any] = {}
    if block.page is not None:
        location["page"] = block.page
    if block.slide is not None:
        location["slide"] = block.slide
    if block.section is not None:
        location["section"] = block.section
    return location


def _chunk_source(atoms: list[Atom]) -> ChunkSource:
    """MIXED if a chunk merges native + OCR; else the single source it carries."""
    sources = {atom.source for atom in atoms}
    has_native = TextBlockSource.NATIVE in sources
    has_ocr = TextBlockSource.OCR in sources
    if has_native and has_ocr:
        return ChunkSource.MIXED
    if has_ocr:
        return ChunkSource.OCR
    return ChunkSource.TEXT
