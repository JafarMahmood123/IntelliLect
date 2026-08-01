"""The per-session store of finished ideas, and the pipeline tee that fills it.

Two properties matter: ideas never leak between sessions (one lecture must not be quizzed on
another's material), and the store is bounded so a long-running process does not accumulate every
idea of every lecture it has ever hosted.
"""

from __future__ import annotations

from uuid import uuid4

from app.application.services.last_idea_store import LastIdeaStore
from app.domain.idea.boundary_trigger import BoundaryTrigger
from app.domain.idea.completed_idea import CompletedIdea


_next_start = 0


def _idea(text: str) -> CompletedIdea:
    """Consecutive, non-overlapping spans, as the buffer emits them — the span identifies an idea
    for the used-already check, so shared spans would conflate different explanations."""
    global _next_start
    _next_start += 1000
    return CompletedIdea(text, _next_start, _next_start + 900, 1, BoundaryTrigger.PAUSE)


def test_latest_is_the_most_recently_recorded():
    session_id = uuid4()
    store = LastIdeaStore()

    store.record(session_id, _idea("first"))
    store.record(session_id, _idea("second"))

    assert store.latest(session_id).text == "second"


def test_latest_is_none_before_any_boundary_has_fired():
    assert LastIdeaStore().latest(uuid4()) is None


def test_recent_is_ordered_oldest_first():
    session_id = uuid4()
    store = LastIdeaStore(history=3)

    for text in ("first", "second", "third"):
        store.record(session_id, _idea(text))

    assert [i.text for i in store.recent(session_id)] == ["first", "second", "third"]


def test_history_is_bounded_and_evicts_the_oldest():
    session_id = uuid4()
    store = LastIdeaStore(history=2)

    for text in ("first", "second", "third"):
        store.record(session_id, _idea(text))

    assert [i.text for i in store.recent(session_id)] == ["second", "third"]


def test_history_of_zero_is_clamped_to_one():
    """A store that retained nothing would make generation permanently impossible."""
    session_id = uuid4()
    store = LastIdeaStore(history=0)

    store.record(session_id, _idea("only"))

    assert store.latest(session_id).text == "only"


def test_sessions_do_not_share_ideas():
    mine, theirs = uuid4(), uuid4()
    store = LastIdeaStore()

    store.record(theirs, _idea("their lecture"))

    assert store.latest(mine) is None
    assert store.recent(mine) == []


def test_release_drops_only_that_session():
    mine, theirs = uuid4(), uuid4()
    store = LastIdeaStore()
    store.record(mine, _idea("mine"))
    store.record(theirs, _idea("theirs"))

    store.release(mine)

    assert store.latest(mine) is None
    assert store.latest(theirs).text == "theirs"


def test_release_is_idempotent():
    """Pipeline teardown runs in a finally block that can be reached more than once."""
    store = LastIdeaStore()
    session_id = uuid4()

    store.release(session_id)
    store.release(session_id)  # must not raise


# --- ideas already turned into a quiz ------------------------------------------


def test_used_ideas_are_left_out_when_asked_for_fresh_ones():
    session_id = uuid4()
    store = LastIdeaStore()
    first, second = _idea("caches"), _idea("eviction")
    store.record(session_id, first)
    store.record(session_id, second)

    store.mark_used(session_id, [first])

    assert [i.text for i in store.recent(session_id, include_used=False)] == ["eviction"]
    # The full history is unchanged: answering a teacher's own question still needs the context.
    assert [i.text for i in store.recent(session_id)] == ["caches", "eviction"]


def test_marking_the_same_idea_twice_is_harmless():
    session_id = uuid4()
    store = LastIdeaStore()
    idea = _idea("caches")
    store.record(session_id, idea)

    store.mark_used(session_id, [idea])
    store.mark_used(session_id, [idea])

    assert store.recent(session_id, include_used=False) == []


def test_used_markers_do_not_leak_between_sessions():
    first_session, second_session = uuid4(), uuid4()
    store = LastIdeaStore()
    shared_text = _idea("caches")
    store.record(first_session, shared_text)
    store.record(second_session, _idea("caches"))

    store.mark_used(first_session, [shared_text])

    assert store.recent(first_session, include_used=False) == []
    assert len(store.recent(second_session, include_used=False)) == 1


def test_used_markers_are_forgotten_once_the_idea_ages_out():
    """Otherwise a lecture running for hours accumulates a marker per idea forever, for ideas that
    can never be offered again anyway."""
    session_id = uuid4()
    store = LastIdeaStore(history=2)
    oldest = _idea("caches")
    store.record(session_id, oldest)
    store.mark_used(session_id, [oldest])

    store.record(session_id, _idea("eviction"))
    store.record(session_id, _idea("hit rate"))

    assert store._used.get(session_id) in (None, set())


def test_release_forgets_used_markers_too():
    session_id = uuid4()
    store = LastIdeaStore()
    idea = _idea("caches")
    store.record(session_id, idea)
    store.mark_used(session_id, [idea])

    store.release(session_id)
    store.record(session_id, _idea("caches again"))

    assert len(store.recent(session_id, include_used=False)) == 1
