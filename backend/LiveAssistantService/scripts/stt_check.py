"""Run a WAV through FakeAudioSource -> FasterWhisperSpeechToText and print each
TranscriptSegment as it arrives — eyeball transcript quality and pause detection on
real English audio with NO LiveKit and NO Ollama.

Usage (from the LiveAssistantService directory):

    python scripts/stt_check.py path/to/english.wav

    # override the model without a .env:
    STT_MODEL=small.en python scripts/stt_check.py path/to/english.wav

The WAV may be any sample rate / channel count — FakeAudioSource normalizes it to
TARGET_SAMPLE_RATE/mono first. The faster-whisper model downloads on first run
(needs network); subsequent runs use the local HuggingFace cache. Requires the
`faster-whisper` engine to be installed (see pyproject / README).
"""

from __future__ import annotations

import argparse
import asyncio
import sys
from pathlib import Path

# Allow running directly from source without an editable install.
sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

from app.infrastructure.audio.fake_audio_source import FakeAudioSource
from app.infrastructure.config.settings import get_settings
from app.infrastructure.stt.faster_whisper_speech_to_text import (
    FasterWhisperSpeechToText,
)


async def _run(wav_path: str) -> int:
    settings = get_settings()
    print(
        f"[stt] model={settings.stt_model!r} device={settings.stt_device} "
        f"compute={settings.stt_compute_type} chunk={settings.stt_chunk_seconds}s "
        f"pause={settings.stt_pause_seconds}s"
    )
    print(f"[stt] source={wav_path} (normalized to "
          f"{settings.target_sample_rate}Hz/{settings.target_channels}ch)")

    source = FakeAudioSource(settings, wav_path)
    stt = FasterWhisperSpeechToText(settings)

    finals = 0
    interims = 0
    print("-" * 72)
    async for seg in stt.transcribe(source.frames()):
        kind = "FINAL " if seg.is_final else "interim"
        pause = "  <pause>" if seg.followed_by_pause else ""
        print(f"[{kind}] {seg.start_ms:>7}-{seg.end_ms:<7}ms{pause}  {seg.text}")
        if seg.is_final:
            finals += 1
        else:
            interims += 1
    print("-" * 72)
    print(f"[stt] done: {finals} final segment(s), {interims} interim(s).")
    return 0 if finals > 0 else 2


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description="Streaming STT check (offline, no LiveKit).")
    parser.add_argument("wav", help="Path to an English WAV file.")
    args = parser.parse_args(argv)

    if not Path(args.wav).is_file():
        print(f"WAV not found: {args.wav}", file=sys.stderr)
        return 1

    try:
        import faster_whisper  # noqa: F401  # fail early with a clear message
    except ImportError:
        print(
            "faster-whisper is not installed. Install the project deps "
            "(pip install -e '.[dev]' or pip install .) and retry.",
            file=sys.stderr,
        )
        return 1

    return asyncio.run(_run(args.wav))


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
