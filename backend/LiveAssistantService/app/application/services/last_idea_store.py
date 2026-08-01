"""The most recent ideas a teacher finished, kept per session so they can be quizzed on.

``CompletedIdea``s are transient in the live loop (LA-3 emits one, LA-4 evaluates it, and it is
dropped). Quiz generation needs to answer "what was the teacher just explaining?" at an arbitrary
moment — when the teacher presses Generate — so the pipeline tees each idea in here as it passes.

Process-wide and built once at startup, exactly like ``FeedbackRecorder``: every session pipeline
writes into the same instance the internal endpoint reads from.

Deliberately NOT durable. An idea is only interesting while the lecture that produced it is still
running, and a restart drops the live sessions along with it. The transcript remains the durable
record (S-0).
"""

from __future__ import annotations

from collections import deque
from uuid import UUID

from app.domain.idea.completed_idea import CompletedIdea

# How many recent ideas to keep per session. More than one because a boundary can fire on a pause
# or the minimum-token cap, making the newest idea a few seconds long — too thin to build several
# questions from. The generator starts at the newest and reaches further back only when it must.
DEFAULT_HISTORY = 3


def _key(idea: CompletedIdea) -> tuple[int, int]:
    """Identity of an idea within a session.

    ``CompletedIdea`` carries no id — it is a transient value in the live loop. Its span is a
    natural key instead: the buffer emits one idea per boundary, so two ideas in the same session
    cannot share a start and an end. Keyed on the span rather than the text so that a teacher who
    repeats themselves word for word still produces a new, quizzable idea.
    """
    return (idea.start_ms, idea.end_ms)


class LastIdeaStore:
    """Bounded per-session history of finished ideas, newest last, minus the ones already quizzed."""

    def __init__(self, history: int = DEFAULT_HISTORY) -> None:
        self._history = max(1, history)
        self._by_session: dict[UUID, deque[CompletedIdea]] = {}
        # Spans already turned into a quiz. Bounded by the same eviction as the ideas themselves:
        # an idea that has fallen out of the history can never be offered again, so remembering
        # that it was used would be dead weight in a process that runs for days.
        self._used: dict[UUID, set[tuple[int, int]]] = {}

    def record(self, session_id: UUID, idea: CompletedIdea) -> None:
        """Remember one finished idea, evicting the oldest past the history bound."""
        ideas = self._by_session.get(session_id)
        if ideas is None:
            ideas = deque(maxlen=self._history)
            self._by_session[session_id] = ideas
        ideas.append(idea)
        self._prune_used(session_id)

    def latest(self, session_id: UUID) -> CompletedIdea | None:
        """The newest finished idea for the session, or None if no boundary has fired yet."""
        ideas = self._by_session.get(session_id)
        return ideas[-1] if ideas else None

    def recent(self, session_id: UUID, *, include_used: bool = True) -> list[CompletedIdea]:
        """The retained ideas oldest-first. Empty when the session has produced none.

        With ``include_used=False`` the ones already built into a quiz are left out, so pressing
        Generate twice asks about what the teacher has said SINCE, rather than re-quizzing the same
        explanation. That is the whole point of tracking them.
        """
        ideas = list(self._by_session.get(session_id, ()))
        if include_used:
            return ideas
        used = self._used.get(session_id, frozenset())
        return [idea for idea in ideas if _key(idea) not in used]

    def mark_used(self, session_id: UUID, ideas: list[CompletedIdea]) -> None:
        """Record that these ideas have been quizzed, so they are not offered again.

        Deliberately NOT called when the teacher generates a single extra question: those all feed
        one draft about one explanation, and consuming an idea per press would make the second
        press of a button fail on a session that has only just started saying anything.
        """
        if not ideas:
            return
        self._used.setdefault(session_id, set()).update(_key(idea) for idea in ideas)
        self._prune_used(session_id)

    def release(self, session_id: UUID) -> None:
        """Drop a session's ideas. Called on pipeline teardown so a long-lived process does not
        accumulate the ideas of every lecture it has ever run."""
        self._by_session.pop(session_id, None)
        self._used.pop(session_id, None)

    def _prune_used(self, session_id: UUID) -> None:
        """Forget used-markers for ideas that have aged out of the history."""
        used = self._used.get(session_id)
        if not used:
            return
        live = {_key(idea) for idea in self._by_session.get(session_id, ())}
        used &= live
        if not used:
            self._used.pop(session_id, None)
