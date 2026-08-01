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


def _idea(text: str) -> CompletedIdea:
    return CompletedIdea(text, 0, 1000, 1, BoundaryTrigger.PAUSE)


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
