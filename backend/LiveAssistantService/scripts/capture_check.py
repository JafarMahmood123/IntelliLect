"""Exercise the AudioSource + normalization path and print frame statistics.

Default (offline, NO LiveKit, NO models) — uses FakeAudioSource:

    python scripts/capture_check.py                 # synthesized 440Hz tone
    python scripts/capture_check.py path/to/file.wav  # any 16-bit WAV (any rate/channels)

It prints the number of AudioFrames received, their total duration, and the sample
rate / channel count — proving the source yields correctly-normalized mono frames at
TARGET_SAMPLE_RATE with no live session.

LiveKit mode (DEFERRED — requires LiveKit credentials in the environment/.env AND a
live room with the teacher already/soon publishing audio; run this only once the
stack is up):

    python scripts/capture_check.py --livekit \
        --room <room_name> --teacher <teacher_identity> \
        [--classroom <uuid>] [--seconds 10]

It connects as AGENT_IDENTITY, subscribes to the teacher's audio only, captures for
--seconds, and prints the same frame stats.
"""

from __future__ import annotations

import argparse
import asyncio
import sys
from pathlib import Path
from uuid import UUID, uuid4

# Allow running directly from source (`python scripts/capture_check.py`) without an
# editable install by putting the project root on sys.path. Harmless when the
# package is pip-installed (as in the container).
sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

from app.application.ports.audio_source import AudioSource
from app.domain.entities.session_context import SessionContext
from app.infrastructure.audio.fake_audio_source import FakeAudioSource
from app.infrastructure.audio.livekit_audio_source import LiveKitAudioSource
from app.infrastructure.config.settings import get_settings


async def _drain_and_report(source: AudioSource, session: SessionContext | None) -> int:
    settings = get_settings()
    if session is not None:
        await source.connect(session)

    count = 0
    total_samples = 0
    sample_rate = settings.target_sample_rate
    channels = settings.target_channels
    first_ts: float | None = None
    last_ts = 0.0
    try:
        async for frame in source.frames():
            count += 1
            total_samples += frame.num_samples
            sample_rate = frame.sample_rate
            channels = frame.channels
            if first_ts is None:
                first_ts = frame.timestamp
            last_ts = frame.timestamp + frame.duration_seconds
    finally:
        await source.disconnect()

    duration = total_samples / sample_rate if sample_rate else 0.0
    print(f"frames received : {count}")
    print(f"sample rate     : {sample_rate} Hz")
    print(f"channels        : {channels} ({'mono' if channels == 1 else 'multi'})")
    print(f"total audio     : {duration:.3f} s ({total_samples} samples)")
    print(f"stream time     : {first_ts or 0.0:.3f}s -> {last_ts:.3f}s")

    ok = count > 0 and sample_rate == settings.target_sample_rate and channels == 1
    print("RESULT          :", "OK" if ok else "UNEXPECTED FORMAT")
    return 0 if ok else 2


async def _run_fake(wav_path: str | None) -> int:
    settings = get_settings()
    print(f"[fake] target={settings.target_sample_rate}Hz/{settings.target_channels}ch "
          f"source={'tone' if wav_path is None else wav_path}")
    source = FakeAudioSource(settings, wav_path)
    # FakeAudioSource.connect is a no-op; a session is not required.
    return await _drain_and_report(source, session=None)


async def _run_livekit(args: argparse.Namespace) -> int:
    settings = get_settings()
    if not settings.livekit_configured:
        print(
            "LiveKit is not configured. Set LIVEKIT_URL / LIVEKIT_API_KEY / "
            "LIVEKIT_API_SECRET (e.g. in .env) and try again.",
            file=sys.stderr,
        )
        return 1

    classroom_id = UUID(args.classroom) if args.classroom else uuid4()
    session = SessionContext(
        session_id=uuid4(),
        classroom_id=classroom_id,
        teacher_identity=args.teacher,
        room_name=args.room,
    )
    print(f"[livekit] joining room={args.room!r} as {settings.agent_identity!r}, "
          f"watching teacher={args.teacher!r} for {args.seconds}s")

    source = LiveKitAudioSource(settings)
    # Bound the capture window so the CLI terminates on its own.
    try:
        return await asyncio.wait_for(
            _drain_and_report(source, session), timeout=args.seconds
        )
    except asyncio.TimeoutError:
        # Expected: we captured for --seconds then stopped. Report is printed by the
        # cancelled task's finally via disconnect; summarize the stop here.
        print(f"[livekit] capture window of {args.seconds}s elapsed.")
        await source.disconnect()
        return 0


def _parse_args(argv: list[str]) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="AudioSource capture check.")
    parser.add_argument(
        "wav", nargs="?", default=None,
        help="Path to a 16-bit WAV for FakeAudioSource (default: synthesized tone).",
    )
    parser.add_argument(
        "--livekit", action="store_true",
        help="Connect to a real LiveKit room instead of using FakeAudioSource.",
    )
    parser.add_argument("--room", help="LiveKit room name (--livekit).")
    parser.add_argument("--teacher", help="Teacher participant identity (--livekit).")
    parser.add_argument("--classroom", help="Classroom UUID (--livekit, optional).")
    parser.add_argument(
        "--seconds", type=float, default=10.0,
        help="Capture window in seconds for --livekit mode (default: 10).",
    )
    return parser.parse_args(argv)


def main(argv: list[str]) -> int:
    args = _parse_args(argv)
    if args.livekit:
        if not args.room or not args.teacher:
            print("--livekit requires --room and --teacher.", file=sys.stderr)
            return 1
        return asyncio.run(_run_livekit(args))
    return asyncio.run(_run_fake(args.wav))


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
