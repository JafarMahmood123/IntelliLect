"""Pacing, safety & suppression gate (LA-7).

Sits between evaluation (LA-4) and delivery (LA-5): given a would-be
``TeacherSuggestion``, decide whether it actually reaches the teacher, so the
assistant never floods or annoys. It only GATES delivery — it does not change
evaluation or delivery mechanics.

Rules, applied in order (first failing rule wins):
  1. LOW_CONFIDENCE — confidence below the minimum.
  2. SESSION_CAP    — per-session delivery cap reached (0 = no cap).
  3. RATE_LIMITED   — too soon after the last delivered suggestion.
  4. DUPLICATE      — near-identical to one delivered within the dedup window.

State is kept PER SESSION (keyed by session_id) and cleared on session end via
``reset``. Time comes from an INJECTED clock (seconds, monotonic) so tests are
deterministic with no real sleeps. Similarity is an injected callable (default token
Jaccard) so it can be upgraded to embeddings later without touching the rules.

Pure application logic: depends only on domain types + injected callables; config is
unpacked from ``Settings`` by the DI factory.
"""

from __future__ import annotations

import time
from collections.abc import Callable
from dataclasses import dataclass, field
from uuid import UUID

from app.application.services.text_similarity import jaccard_similarity
from app.domain.evaluation.feedback_type import FeedbackType
from app.domain.evaluation.teacher_suggestion import TeacherSuggestion
from app.domain.pacing.pacing_decision import PacingDecision
from app.domain.pacing.suppression_reason import SuppressionReason


@dataclass
class _DeliveredEntry:
    at: float
    text: str
    citations: frozenset[int]
    type: FeedbackType


@dataclass
class _SessionState:
    last_delivered: float | None = None
    delivered_count: int = 0
    history: list[_DeliveredEntry] = field(default_factory=list)


class FeedbackPacer:
    def __init__(
        self,
        *,
        min_interval_sec: float,
        confidence_min: float,
        dedup_window_sec: float,
        dedup_similarity: float,
        max_per_session: int,
        clock: Callable[[], float] = time.monotonic,
        similarity: Callable[[str, str], float] = jaccard_similarity,
    ) -> None:
        self._min_interval = min_interval_sec
        self._confidence_min = confidence_min
        self._dedup_window = dedup_window_sec
        self._dedup_similarity = dedup_similarity
        self._max_per_session = max_per_session
        self._clock = clock
        self._similarity = similarity
        self._sessions: dict[UUID, _SessionState] = {}

    def decide(
        self,
        session_id: UUID,
        suggestion: TeacherSuggestion,
        now: float | None = None,
    ) -> PacingDecision:
        """Gate one suggestion for a session. ``now`` defaults to the injected clock."""
        if now is None:
            now = self._clock()
        state = self._sessions.setdefault(session_id, _SessionState())
        self._expire(state, now)

        # 1. Low confidence.
        if suggestion.confidence < self._confidence_min:
            return PacingDecision.suppressed(SuppressionReason.LOW_CONFIDENCE)

        # 2. Per-session cap.
        if self._max_per_session > 0 and state.delivered_count >= self._max_per_session:
            return PacingDecision.suppressed(SuppressionReason.SESSION_CAP)

        # 3. Rate limit.
        if (
            state.last_delivered is not None
            and now - state.last_delivered < self._min_interval
        ):
            return PacingDecision.suppressed(SuppressionReason.RATE_LIMITED)

        # 4. Duplicate of a recently delivered suggestion.
        if self._is_duplicate(state, suggestion):
            return PacingDecision.suppressed(SuppressionReason.DUPLICATE)

        # Deliver: record it so it gates future suggestions.
        state.last_delivered = now
        state.delivered_count += 1
        state.history.append(
            _DeliveredEntry(now, suggestion.text, frozenset(suggestion.citations), suggestion.type)
        )
        return PacingDecision.delivered()

    def reset(self, session_id: UUID) -> None:
        """Drop a session's pacing state (called on session end)."""
        self._sessions.pop(session_id, None)

    def active_sessions(self) -> int:
        return len(self._sessions)

    def _expire(self, state: _SessionState, now: float) -> None:
        cutoff = now - self._dedup_window
        state.history = [entry for entry in state.history if entry.at >= cutoff]

    def _is_duplicate(self, state: _SessionState, suggestion: TeacherSuggestion) -> bool:
        citations = frozenset(suggestion.citations)
        for entry in state.history:
            # Same topic + same cited sources -> a repeat of the same point.
            if (
                entry.type is suggestion.type
                and citations
                and citations == entry.citations
            ):
                return True
            # Or near-identical wording.
            if self._similarity(suggestion.text, entry.text) >= self._dedup_similarity:
                return True
        return False
