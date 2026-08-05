"""Drive the FeedbackPacer (LA-7) with a scripted sequence of (suggestion, timestamp)
inputs and print each PacingDecision — fully OFFLINE, NO models, NO real sleeps.

Demonstrates: a burst of flagged suggestions gets rate-limited, a low-confidence one
is dropped, and a repeat of a recent suggestion is deduped.

    python scripts/pacing_check.py
"""

from __future__ import annotations

import sys
from pathlib import Path
from uuid import uuid4

# Allow running directly from source without an editable install.
sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

from app.api.dependencies import build_feedback_pacer
from app.domain.evaluation.feedback_type import FeedbackType
from app.domain.evaluation.teacher_suggestion import TeacherSuggestion
from app.infrastructure.config.settings import get_settings


def _sug(text, ftype, citations, confidence) -> TeacherSuggestion:
    return TeacherSuggestion(text=text, type=ftype, citations=citations, sources=[], confidence=confidence)


# (now_seconds, label, suggestion)
def _script() -> list[tuple[float, str, TeacherSuggestion]]:
    disc = FeedbackType.DISCREPANCY
    return [
        (0, "first suggestion",
         _sug("Photosynthesis occurs in the chloroplast, not the mitochondria [1].", disc, [1], 0.9)),
        (10, "burst (too soon)",
         _sug("Gravity is a force between masses [2].", FeedbackType.GAP, [2], 0.9)),
        (60, "low confidence",
         _sug("Maybe reconsider the phrasing here [3].", FeedbackType.LIKELY, [3], 0.30)),
        (120, "repeat of #1",
         _sug("The chloroplast, not the mitochondria, is where photosynthesis happens [1].", disc, [1], 0.9)),
        (200, "new, spaced out",
         _sug("The light reactions occur in the thylakoid membrane [4].", FeedbackType.GAP, [4], 0.8)),
    ]


def main() -> int:
    settings = get_settings()
    pacer = build_feedback_pacer(settings)
    session_id = uuid4()

    print("[pacing] one session, scripted (suggestion, t) inputs — NO models, NO sleeps")
    print(f"[pacing] min_interval={settings.feedback_min_interval_sec}s "
          f"confidence_min={settings.feedback_confidence_min} "
          f"dedup_window={settings.feedback_dedup_window_sec}s "
          f"dedup_similarity={settings.feedback_dedup_similarity}")
    print("-" * 78)
    delivered = 0
    for now, label, suggestion in _script():
        decision = pacer.decide(session_id, suggestion, now=now)
        verb = "DELIVER " if decision.deliver else "suppress"
        delivered += 1 if decision.deliver else 0
        print(f"t={now:>4.0f}s  {verb}  reason={decision.reason.value:<13} "
              f"conf={suggestion.confidence:.2f}  ({label})")
    print("-" * 78)
    print(f"[pacing] delivered {delivered}/{len(_script())} suggestions.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
