"""Segment a transcript into ideas with the LA-3 BoundaryDetector and print each
CompletedIdea (trigger, tokens, duration, text).

Default mode is fully OFFLINE — NO STT model, NO Ollama. It drives the detector with
a hardcoded, scripted transcript (a clear topic shift, a stray one-word fragment, a
capped monologue, and a trailing idea) and a deterministic keyword embedder, proving
the boundary logic end to end:

    python scripts/boundary_check.py

``--live`` chains the REAL pipeline (LA-2 faster-whisper over a WAV via FakeAudioSource
+ the real Ollama embedder) — DEFERRED: it needs the STT model and a running Ollama
with the embedding model pulled:

    python scripts/boundary_check.py --live path/to/english.wav
"""

from __future__ import annotations

import argparse
import asyncio
import sys
from collections.abc import AsyncIterator
from pathlib import Path

# Allow running directly from source without an editable install.
sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

from app.api.dependencies import build_boundary_detector
from app.application.ports.embedding_provider import EmbeddingProvider
from app.application.services.token_estimate import estimate_tokens
from app.domain.idea.completed_idea import CompletedIdea
from app.domain.transcript.transcript_segment import TranscriptSegment
from app.infrastructure.config.settings import get_settings


# --- Offline demo fakes (self-contained so this script ships without tests/) -------
class _KeywordEmbeddingProvider(EmbeddingProvider):
    """One-hot vector per topic keyword; a default axis for unmatched text."""

    def __init__(self, keywords: list[str]) -> None:
        dim = len(keywords) + 1
        self._topics = {
            kw.lower(): [1.0 if i == idx else 0.0 for i in range(dim)]
            for idx, kw in enumerate(keywords)
        }
        self._default = [1.0 if i == len(keywords) else 0.0 for i in range(dim)]

    async def embed_query(self, text: str) -> list[float]:
        lowered = text.lower()
        for keyword, vector in self._topics.items():
            if keyword in lowered:
                return list(vector)
        return list(self._default)


def _seg(text, start_ms, end_ms, *, final=True, pause=False) -> TranscriptSegment:
    return TranscriptSegment(text, start_ms, end_ms, is_final=final, followed_by_pause=pause)


def _scripted_transcript() -> list[TranscriptSegment]:
    """A scripted stream exercising every boundary trigger + min-token merge."""
    return [
        # An interim segment is ignored (drift is only measured on final text).
        _seg("Photosynthesis is", 0, 1000, final=False),
        # Idea 1 — photosynthesis (closed by DRIFT when the topic shifts).
        _seg("Photosynthesis converts sunlight into chemical energy", 0, 2000),
        _seg("Chlorophyll captures photons to drive photosynthesis reactions", 2000, 4000),
        # Topic shift -> DRIFT boundary; this segment starts idea 2.
        _seg("Newton described gravity as an attractive force between masses", 4000, 6000),
        # Idea 2 — gravity (closed by PAUSE).
        _seg("Gravity also explains the ocean tides on Earth", 6000, 8000, pause=True),
        # A stray one-word fragment: below min tokens, must merge FORWARD (no own idea).
        _seg("Okay", 8000, 8600, pause=True),
        # Idea 3 — history monologue (closed by TOKEN_CAP), swallows "Okay".
        _seg("Now let's discuss the history of the Roman empire", 8600, 10000),
        _seg("The Roman history spans over a thousand years of conquest", 10000, 11500),
        _seg("Roman history includes the republic and the empire periods", 11500, 13000),
        # Idea 4 — trailing idea, no closing trigger -> flushed at stream end.
        _seg("In summary photosynthesis and gravity are core science topics", 13000, 15000),
    ]


async def _as_stream(segments: list[TranscriptSegment]) -> AsyncIterator[TranscriptSegment]:
    for segment in segments:
        yield segment


def _print_idea(index: int, idea: CompletedIdea) -> None:
    tokens = estimate_tokens(idea.text)
    duration = idea.duration_ms / 1000.0
    print(
        f"idea {index}: trigger={idea.trigger.value:<8} "
        f"tokens={tokens:<3} segs={idea.segment_count} dur={duration:>5.1f}s "
        f"[{idea.start_ms}-{idea.end_ms}ms]"
    )
    print(f"         {idea.text}")


async def _run_offline() -> int:
    # Demo-friendly caps so a short scripted monologue visibly trips TOKEN_CAP.
    settings = get_settings().model_copy(
        update={"boundary_max_tokens": 25, "boundary_min_tokens": 4, "boundary_max_seconds": 30.0}
    )
    embedder = _KeywordEmbeddingProvider(["photosynthesis", "gravity", "history"])
    detector = build_boundary_detector(settings, embedder)

    print("[offline] scripted transcript -> BoundaryDetector (NO STT, NO Ollama)")
    print(f"[offline] drift>={settings.boundary_drift_threshold} "
          f"min_tokens={settings.boundary_min_tokens} max_tokens={settings.boundary_max_tokens} "
          f"max_seconds={settings.boundary_max_seconds}")
    print("-" * 72)
    count = 0
    async for idea in detector.process(_as_stream(_scripted_transcript())):
        count += 1
        _print_idea(count, idea)
    print("-" * 72)
    print(f"[offline] {count} idea(s) detected.")
    return 0 if count > 0 else 2


async def _run_live(wav_path: str) -> int:
    from app.api.dependencies import build_embedding_provider
    from app.infrastructure.audio.fake_audio_source import FakeAudioSource
    from app.infrastructure.stt.faster_whisper_speech_to_text import (
        FasterWhisperSpeechToText,
    )

    settings = get_settings()
    print(f"[live] STT({settings.stt_model}) over {wav_path} + Ollama({settings.embedding_model})")
    source = FakeAudioSource(settings, wav_path)
    stt = FasterWhisperSpeechToText(settings)
    embedder = build_embedding_provider(settings)
    detector = build_boundary_detector(settings, embedder)

    print("-" * 72)
    count = 0
    async for idea in detector.process(stt.transcribe(source.frames())):
        count += 1
        _print_idea(count, idea)
    print("-" * 72)
    print(f"[live] {count} idea(s) detected.")
    return 0 if count > 0 else 2


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description="Idea boundary detection check (LA-3).")
    parser.add_argument(
        "--live", metavar="WAV",
        help="DEFERRED: run the real STT (over this WAV) + Ollama embedder instead of the "
             "offline scripted demo. Needs the STT model and a running Ollama.",
    )
    args = parser.parse_args(argv)

    if args.live:
        if not Path(args.live).is_file():
            print(f"WAV not found: {args.live}", file=sys.stderr)
            return 1
        return asyncio.run(_run_live(args.live))
    return asyncio.run(_run_offline())


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
