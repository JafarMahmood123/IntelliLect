"""Publish→subscribe audio transit through the LiveKit SFU (work-plan §9.1, hop L-4).

**Read the name carefully: this is not glass-to-glass.** Glass-to-glass is what the
work plan asks for and it is what a user experiences, but it cannot be measured from
here, because the two largest terms in it happen inside a browser this harness is not:

  microphone capture buffer  →  [ encode → SFU → decode ]  →  playout jitter buffer
        ~10-40ms, browser        ← what this measures →         ~40-200ms, adaptive

The jitter buffer alone is usually larger than everything else combined, and it is
adaptive, so it cannot be assumed either. What this probe gives is the **floor**: the
part of the path that is our deployment's responsibility, and the only part a change
to our infrastructure can move. A number reported as glass-to-glass when it excludes
the jitter buffer would understate the real figure by a factor of two or more — the
report has to say which one it is.

The method: publish silence, then a full-scale tone, from one participant; subscribe
from a second and stamp the first frame whose RMS crosses a threshold. The interval is
publish-side capture to subscribe-side delivery, on one process clock, so there is no
container/host skew in it.

The SDK calls here mirror `LiveAssistantService/app/infrastructure/audio/
livekit_audio_source.py`, which does the same subscription against the same SDK
version in production — that file is the reference for anything version-sensitive.
"""

from __future__ import annotations

import asyncio
import contextlib
import logging
import time

import numpy as np
from livekit import rtc

logger = logging.getLogger("e2e.probe")

SAMPLE_RATE = 48000
CHANNELS = 1
FRAME_MS = 10
_SAMPLES_PER_FRAME = SAMPLE_RATE * FRAME_MS // 1000

# Full-scale-ish tone against digital silence. The threshold sits far above any
# codec ringing on the silent frames and far below the tone, so "did the marker
# arrive" never becomes a judgement call about a borderline frame.
_TONE_AMPLITUDE = 0.8
_SILENCE_LEAD_FRAMES = 30  # 300ms, enough for the subscriber to be settled
_TONE_FRAMES = 20  # 200ms of tone
_RMS_THRESHOLD = 0.2


def _tone_frame(phase: int) -> rtc.AudioFrame:
    t = (np.arange(_SAMPLES_PER_FRAME) + phase) / SAMPLE_RATE
    pcm = (np.sin(2 * np.pi * 440.0 * t) * _TONE_AMPLITUDE * 32767).astype(np.int16)
    return rtc.AudioFrame(
        data=pcm.tobytes(),
        sample_rate=SAMPLE_RATE,
        num_channels=CHANNELS,
        samples_per_channel=_SAMPLES_PER_FRAME,
    )


def _silent_frame() -> rtc.AudioFrame:
    return rtc.AudioFrame(
        data=b"\x00" * (_SAMPLES_PER_FRAME * CHANNELS * 2),
        sample_rate=SAMPLE_RATE,
        num_channels=CHANNELS,
        samples_per_channel=_SAMPLES_PER_FRAME,
    )


def _rms(frame) -> float:
    pcm = np.frombuffer(bytes(frame.data), dtype=np.int16).astype(np.float32) / 32768.0
    return float(np.sqrt(np.mean(np.square(pcm)))) if pcm.size else 0.0


class TonePublisher:
    """A participant that publishes a mic track and can emit a detectable marker."""

    def __init__(self, ws_url: str, token: str) -> None:
        self._ws_url, self._token = ws_url, token
        self._room = rtc.Room()
        self._source: rtc.AudioSource | None = None

    async def __aenter__(self) -> "TonePublisher":
        await self._room.connect(self._ws_url, self._token)
        self._source = rtc.AudioSource(SAMPLE_RATE, CHANNELS)
        track = rtc.LocalAudioTrack.create_audio_track("probe-mic", self._source)
        await self._room.local_participant.publish_track(
            track, rtc.TrackPublishOptions(source=rtc.TrackSource.SOURCE_MICROPHONE)
        )
        return self

    async def __aexit__(self, *exc) -> None:
        with contextlib.suppress(Exception):
            await self._room.disconnect()

    async def emit_marker(self) -> float:
        """Silence, then a tone. Returns the perf_counter for the FIRST tone frame.

        The lead-in silence matters: without it the encoder's first packet after a gap
        also pays for track (re)start, and that cost would be attributed to transit.
        """
        assert self._source is not None
        for _ in range(_SILENCE_LEAD_FRAMES):
            await self._source.capture_frame(_silent_frame())
            await asyncio.sleep(FRAME_MS / 1000)

        started_at = time.perf_counter()
        for index in range(_TONE_FRAMES):
            await self._source.capture_frame(_tone_frame(index * _SAMPLES_PER_FRAME))
            await asyncio.sleep(FRAME_MS / 1000)
        return started_at


class ToneSubscriber:
    """A participant that subscribes to the publisher's audio and stamps the marker."""

    def __init__(self, ws_url: str, token: str) -> None:
        self._ws_url, self._token = ws_url, token
        self._room = rtc.Room()
        self._arrivals: asyncio.Queue[float] = asyncio.Queue()
        self._readers: set[asyncio.Task] = set()

    async def __aenter__(self) -> "ToneSubscriber":
        @self._room.on("track_subscribed")
        def _on_track(track, publication, participant) -> None:  # noqa: ANN001
            if getattr(track, "kind", None) == rtc.TrackKind.KIND_AUDIO:
                task = asyncio.create_task(self._read(track))
                self._readers.add(task)
                task.add_done_callback(self._readers.discard)

        await self._room.connect(self._ws_url, self._token)
        return self

    async def __aexit__(self, *exc) -> None:
        for task in list(self._readers):
            task.cancel()
        with contextlib.suppress(Exception):
            await self._room.disconnect()

    async def _read(self, track) -> None:  # noqa: ANN001
        stream = rtc.AudioStream(track)
        above = False
        try:
            async for event in stream:
                # Stamp before the RMS maths, for the same reason the SignalR pump
                # stamps before json.loads.
                arrived_at = time.perf_counter()
                frame = getattr(event, "frame", event)
                loud = _rms(frame) >= _RMS_THRESHOLD
                # Only the RISING edge is a marker; the following 190ms of tone is the
                # same marker still arriving, and counting it would report ~0ms.
                if loud and not above:
                    self._arrivals.put_nowait(arrived_at)
                above = loud
        except asyncio.CancelledError:
            raise
        except Exception:  # noqa: BLE001
            logger.exception("Probe subscriber reader stopped.")
        finally:
            aclose = getattr(stream, "aclose", None)
            if aclose is not None:
                with contextlib.suppress(Exception):
                    await aclose()

    async def wait_for_marker(self, timeout_s: float) -> float:
        return await asyncio.wait_for(self._arrivals.get(), timeout_s)

    def drain(self) -> None:
        while not self._arrivals.empty():
            self._arrivals.get_nowait()
