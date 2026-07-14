from __future__ import annotations

from dataclasses import dataclass
from uuid import UUID


@dataclass(frozen=True)
class SessionContext:
    """Identifies the live session the assistant agent joins and observes.

    Pure domain object: no framework/SDK imports. It carries just enough to (a)
    join the correct LiveKit room and (b) single out the teacher's audio track from
    every other (student) participant.

    `teacher_identity` and `room_name` are LiveKit participant/room identifiers as
    provisioned by the session-lifecycle layer (wired in a later phase, LA-6);
    `classroom_id` is the IntelliLect classroom whose uploaded material the teacher's
    ideas will eventually be checked against (RAG, later phase).
    """

    session_id: UUID
    classroom_id: UUID
    teacher_identity: str  # LiveKit participant identity of the teacher
    room_name: str  # LiveKit room to join
