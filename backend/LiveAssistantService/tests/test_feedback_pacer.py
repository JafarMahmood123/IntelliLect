"""Deterministic FeedbackPacer tests (LA-7) — fake clock via explicit ``now``, no sleeps."""

from __future__ import annotations

from uuid import uuid4

from app.application.services.feedback_pacer import FeedbackPacer
from app.domain.evaluation.feedback_type import FeedbackType
from app.domain.evaluation.teacher_suggestion import TeacherSuggestion
from app.domain.pacing.suppression_reason import SuppressionReason


def _pacer(**overrides) -> FeedbackPacer:
    defaults = dict(
        min_interval_sec=45.0,
        confidence_min=0.5,
        dedup_window_sec=300.0,
        dedup_similarity=0.85,
        max_per_session=0,
    )
    defaults.update(overrides)
    return FeedbackPacer(**defaults)


def _sug(text="a distinct suggestion about something", ftype=FeedbackType.DISCREPANCY,
         citations=(1,), confidence=0.9) -> TeacherSuggestion:
    return TeacherSuggestion(text=text, type=ftype, citations=list(citations), sources=[], confidence=confidence)


# --- RATE LIMITING -----------------------------------------------------------
def test_rate_limits_within_interval_then_delivers_after():
    pacer, sid = _pacer(min_interval_sec=45.0), uuid4()

    first = pacer.decide(sid, _sug(text="alpha one"), now=0)
    second = pacer.decide(sid, _sug(text="beta two", citations=(2,), ftype=FeedbackType.GAP), now=10)
    third = pacer.decide(sid, _sug(text="beta two", citations=(2,), ftype=FeedbackType.GAP), now=50)

    assert first.deliver is True
    assert second.deliver is False and second.reason is SuppressionReason.RATE_LIMITED
    assert third.deliver is True  # 50s since the last DELIVERED (t=0) >= 45s


# --- LOW CONFIDENCE ----------------------------------------------------------
def test_low_confidence_is_suppressed():
    pacer, sid = _pacer(confidence_min=0.5), uuid4()

    decision = pacer.decide(sid, _sug(confidence=0.3), now=0)

    assert decision.deliver is False and decision.reason is SuppressionReason.LOW_CONFIDENCE


def test_missing_confidence_defaults_to_fully_confident():
    pacer, sid = _pacer(confidence_min=0.5), uuid4()
    # TeacherSuggestion built without confidence -> 1.0 domain default -> not low.
    suggestion = TeacherSuggestion("x", FeedbackType.GAP, [1], [])

    assert pacer.decide(sid, suggestion, now=0).deliver is True


# --- DUPLICATE ---------------------------------------------------------------
def test_near_identical_text_is_duplicate():
    pacer, sid = _pacer(min_interval_sec=0.0, dedup_similarity=0.85), uuid4()
    text = "Photosynthesis occurs in the chloroplast not the mitochondria"

    assert pacer.decide(sid, _sug(text=text, citations=(1,)), now=0).deliver is True
    # Same wording, different citation -> caught by text similarity.
    repeat = pacer.decide(sid, _sug(text=text, citations=(9,)), now=1)
    assert repeat.deliver is False and repeat.reason is SuppressionReason.DUPLICATE


def test_same_citations_and_type_is_duplicate_even_with_different_wording():
    pacer, sid = _pacer(min_interval_sec=0.0, dedup_similarity=0.99), uuid4()

    pacer.decide(sid, _sug(text="one phrasing entirely", ftype=FeedbackType.DISCREPANCY, citations=(3,)), now=0)
    repeat = pacer.decide(sid, _sug(text="totally different words here", ftype=FeedbackType.DISCREPANCY, citations=(3,)), now=1)

    assert repeat.deliver is False and repeat.reason is SuppressionReason.DUPLICATE


def test_duplicate_delivers_again_after_window_expires():
    pacer, sid = _pacer(min_interval_sec=0.0, dedup_window_sec=300.0), uuid4()
    text = "The exact same suggestion text repeated"

    assert pacer.decide(sid, _sug(text=text), now=0).deliver is True
    assert pacer.decide(sid, _sug(text=text), now=1).reason is SuppressionReason.DUPLICATE
    # Past the dedup window, the old delivery no longer counts.
    assert pacer.decide(sid, _sug(text=text), now=400).deliver is True


# --- SESSION CAP -------------------------------------------------------------
def test_session_cap_stops_deliveries_at_the_limit():
    pacer, sid = _pacer(min_interval_sec=0.0, max_per_session=2, dedup_similarity=2.0), uuid4()

    d1 = pacer.decide(sid, _sug(text="first distinct", citations=(1,)), now=0)
    d2 = pacer.decide(sid, _sug(text="second distinct", citations=(2,)), now=1)
    d3 = pacer.decide(sid, _sug(text="third distinct", citations=(3,)), now=2)

    assert d1.deliver and d2.deliver
    assert d3.deliver is False and d3.reason is SuppressionReason.SESSION_CAP


# --- ORDERING ----------------------------------------------------------------
def test_low_confidence_wins_over_rate_limit_and_duplicate():
    pacer, sid = _pacer(min_interval_sec=45.0, confidence_min=0.5), uuid4()
    text = "identical wording"

    pacer.decide(sid, _sug(text=text, confidence=0.9), now=0)
    # Would be BOTH rate-limited and a duplicate, but low confidence is checked first.
    decision = pacer.decide(sid, _sug(text=text, confidence=0.2), now=1)

    assert decision.reason is SuppressionReason.LOW_CONFIDENCE


# --- PER-SESSION ISOLATION + CLEANUP -----------------------------------------
def test_sessions_are_isolated():
    pacer = _pacer(min_interval_sec=45.0)
    a, b = uuid4(), uuid4()

    assert pacer.decide(a, _sug(text="alpha"), now=0).deliver is True
    # B has its own state — A's rate limit does not apply.
    assert pacer.decide(b, _sug(text="beta", citations=(2,)), now=1).deliver is True


def test_reset_clears_session_state():
    pacer, sid = _pacer(min_interval_sec=45.0), uuid4()
    text = "same suggestion twice"

    assert pacer.decide(sid, _sug(text=text), now=0).deliver is True
    assert pacer.active_sessions() == 1

    pacer.reset(sid)
    assert pacer.active_sessions() == 0

    # State gone: what would have been rate-limited/duplicate now delivers.
    again = pacer.decide(sid, _sug(text=text), now=1)
    assert again.deliver is True and again.reason is SuppressionReason.NONE
