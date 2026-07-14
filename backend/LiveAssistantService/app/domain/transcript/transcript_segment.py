from __future__ import annotations

from dataclasses import dataclass


@dataclass(frozen=True)
class TranscriptSegment:
    """A recognized span of the teacher's speech.

    Pure domain object: no library imports. Produced by a ``SpeechToText`` port and
    consumed by the (later) idea/boundary detector. Timing is in milliseconds of
    stream time (offset from the start of capture), taken from ``AudioFrame``
    timestamps — not wall-clock — so segments order deterministically.

    An utterance is emitted as zero or more *interim* segments (``is_final=False``,
    the best transcription so far) followed by exactly one *final* segment
    (``is_final=True``). ``followed_by_pause`` is set on a final segment when a
    silence gap (>= ``STT_PAUSE_SECONDS``) closed the utterance — the signal LA-3
    will use to decide the teacher finished an "idea".
    """

    text: str
    start_ms: int
    end_ms: int
    is_final: bool  # interim (partial) vs finalized segment
    followed_by_pause: bool  # a pause >= STT_PAUSE_SECONDS followed this segment

    @property
    def duration_ms(self) -> int:
        return max(0, self.end_ms - self.start_ms)
