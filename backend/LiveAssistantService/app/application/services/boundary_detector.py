"""Idea boundary detection (LA-3).

Consumes the ordered ``TranscriptSegment`` stream (LA-2), accumulates finalized
segments into an "idea buffer", and emits a ``CompletedIdea`` when one coherent idea
has ended. It ONLY detects boundaries and emits ideas — no retrieval, no brain, no
feedback (LA-4+).

Pure application logic: it depends only on the ``EmbeddingProvider`` port and domain
types. Config arrives as primitives (the DI factory reads them from ``Settings``),
keeping this layer free of ``infrastructure`` imports — matching the house style for
application services. The embedder is exercised via a deterministic
``FakeEmbeddingProvider`` in tests, so no live model is ever required.
"""

from __future__ import annotations

import math
from collections.abc import AsyncIterator

from app.application.ports.embedding_provider import EmbeddingProvider
from app.application.services.stream_prefetch import lookahead_map
from app.application.services.token_estimate import estimate_tokens
from app.domain.idea.boundary_trigger import BoundaryTrigger
from app.domain.idea.completed_idea import CompletedIdea
from app.domain.transcript.transcript_segment import TranscriptSegment


def _cosine_distance(a: list[float], b: list[float]) -> float:
    """Cosine distance (1 - cosine similarity) in [0, 2]; 0.0 when either is zero-norm.

    A zero-norm vector carries no direction, so we treat it as "no drift" (distance 0)
    rather than spuriously maximal — real embeddings are never zero.
    """
    dot = sum(x * y for x, y in zip(a, b))
    norm_a = math.sqrt(sum(x * x for x in a))
    norm_b = math.sqrt(sum(y * y for y in b))
    if norm_a == 0.0 or norm_b == 0.0:
        return 0.0
    similarity = dot / (norm_a * norm_b)
    # Guard tiny floating-point overshoot outside [-1, 1].
    similarity = max(-1.0, min(1.0, similarity))
    return 1.0 - similarity


class BoundaryDetector:
    """Segments a transcript stream into ``CompletedIdea``s by drift / pause / caps."""

    def __init__(
        self,
        embedder: EmbeddingProvider,
        *,
        drift_threshold: float,
        pause_seconds: float,
        max_seconds: float,
        max_tokens: int,
        min_tokens: int,
        embed_lookahead: int = 4,
    ) -> None:
        self._embedder = embedder
        self._drift_threshold = drift_threshold
        self._pause_ms = pause_seconds * 1000.0
        self._max_seconds = max_seconds
        self._max_tokens = max_tokens
        self._min_tokens = min_tokens
        # How many segment embeddings may be in flight at once. Bounded because the embedder is a
        # rate-limited hosted API — an unbounded fan-out turns a burst of speech into a burst of 429s.
        self._embed_lookahead = embed_lookahead
        self._reset()

    # -- public ---------------------------------------------------------------
    async def process(
        self, segments: AsyncIterator[TranscriptSegment]
    ) -> AsyncIterator[CompletedIdea]:
        """Yield a ``CompletedIdea`` each time an idea boundary fires.

        Only finalized segments (``is_final=True``) drive decisions — interim text is
        unstable, so drift is never measured on it. On stream end, any buffered idea
        that meets ``min_tokens`` is flushed as a terminal (``PAUSE``) idea.
        """
        prev_end_ms: int | None = None

        # Embeddings overlap; DECISIONS stay strictly ordered. Segment N's vector does not depend on
        # N-1's, but whether N starts a new idea depends on the buffer N-1 left behind — so
        # lookahead_map runs several embedding calls concurrently and hands them back in source
        # order. See stream_prefetch for the measurements that motivated it.
        async for segment, embedding in lookahead_map(
            self._finals_only(segments),
            lambda seg: self._embedder.embed_query(seg.text),
            self._embed_lookahead,
        ):
            gap_ms = 0.0 if prev_end_ms is None else segment.start_ms - prev_end_ms
            prev_end_ms = segment.end_ms

            # (A) A silent gap BEFORE this segment closes the running idea (pause).
            if self._emittable() and gap_ms >= self._pause_ms:
                yield self._close(BoundaryTrigger.PAUSE)

            # (B) Place this segment: drift starts a new idea; otherwise it extends
            #     the current one (or starts the first idea). Tiny fragments below
            #     min_tokens never trigger drift — they merge forward into the next.
            if self._segments and self._emittable() and self._is_drift(embedding):
                completed = self._close(BoundaryTrigger.DRIFT)
                self._start(segment, embedding)
                yield completed
            elif self._segments:
                self._append(segment, embedding)
            else:
                self._start(segment, embedding)

            # (C) A pause AFTER this segment (LA-2 flag) or a safety-net cap closes it.
            trigger = self._closing_trigger(segment)
            if trigger is not None:
                yield self._close(trigger)

        # Flush a trailing idea at session end (treated as a terminal pause).
        if self._emittable():
            yield self._close(BoundaryTrigger.PAUSE)

    @staticmethod
    async def _finals_only(
        segments: AsyncIterator[TranscriptSegment],
    ) -> AsyncIterator[TranscriptSegment]:
        """Drop interim segments before they reach the embedder.

        Interim text is unstable, so drift was never measured on it — filtering here rather than
        inside the loop means an interim segment never costs an embedding call either.
        """
        async for segment in segments:
            if segment.is_final:
                yield segment

    # -- buffer state ---------------------------------------------------------
    def _reset(self) -> None:
        self._segments: list[TranscriptSegment] = []
        self._emb_sum: list[float] = []
        self._emb_count = 0
        self._tokens = 0

    def _start(self, segment: TranscriptSegment, embedding: list[float]) -> None:
        self._segments = [segment]
        self._emb_sum = list(embedding)
        self._emb_count = 1
        self._tokens = estimate_tokens(segment.text)

    def _append(self, segment: TranscriptSegment, embedding: list[float]) -> None:
        self._segments.append(segment)
        if len(embedding) == len(self._emb_sum):
            self._emb_sum = [s + e for s, e in zip(self._emb_sum, embedding)]
            self._emb_count += 1
        self._tokens += estimate_tokens(segment.text)

    def _representative(self) -> list[float]:
        """Running idea vector = mean of the buffer's segment embeddings."""
        if self._emb_count == 0:
            return []
        return [s / self._emb_count for s in self._emb_sum]

    def _emittable(self) -> bool:
        """Whether the current buffer is large enough to be emitted as an idea."""
        return bool(self._segments) and self._tokens >= self._min_tokens

    def _is_drift(self, embedding: list[float]) -> bool:
        return _cosine_distance(self._representative(), embedding) >= self._drift_threshold

    def _duration_seconds(self) -> float:
        return (self._segments[-1].end_ms - self._segments[0].start_ms) / 1000.0

    def _closing_trigger(self, segment: TranscriptSegment) -> BoundaryTrigger | None:
        """Cap that should close the CURRENT buffer, or None.

        A plain ``followed_by_pause`` deliberately does NOT close an idea. The STT finalizes a
        segment after ``STT_PAUSE_SECONDS`` of silence, so essentially EVERY segment arrives with
        that flag set — honouring it made an idea equal one breath, and no idea could ever span
        more than a single sentence regardless of what the drift detector thought. Boundaries are
        semantic (DRIFT, above) and a breath is not a change of subject.

        Silence still ends an idea, but only a genuinely long one: the (A) inter-segment gap check
        in ``process`` uses ``boundary_pause_seconds``, which is meant to be set WELL ABOVE
        ``STT_PAUSE_SECONDS`` — a real "…and that's the end of that point" pause, not a breath.
        The caps below remain unconditional safety nets.
        """
        if not self._emittable():
            return None
        if self._duration_seconds() >= self._max_seconds:
            return BoundaryTrigger.TIME_CAP
        if self._tokens >= self._max_tokens:
            return BoundaryTrigger.TOKEN_CAP
        return None

    def _close(self, trigger: BoundaryTrigger) -> CompletedIdea:
        text = " ".join(seg.text.strip() for seg in self._segments).strip()
        idea = CompletedIdea(
            text=text,
            start_ms=self._segments[0].start_ms,
            end_ms=self._segments[-1].end_ms,
            segment_count=len(self._segments),
            trigger=trigger,
            representative_vector=self._representative(),
        )
        self._reset()
        return idea
