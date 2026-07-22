"""A synthetic 'teacher' LiveKit participant for the E2E.

It joins the real room with the teacher's own join token (so its participant
identity == the `teacherIdentity` the agent watches), publishes a spoken WAV as a
microphone track, and listens for the feedback data packet the agent sends back to
the teacher identity (topic "teaching_suggestion").

The LiveKit Python SDK evolves quickly, so every version-sensitive call is guarded.
This is the same SDK the LiveAssistant service depends on (`livekit>=1.0`).
"""

from __future__ import annotations

import asyncio
import contextlib
import json
import logging
import wave
from pathlib import Path

from livekit import rtc

logger = logging.getLogger("e2e.teacher")

_FRAME_MS = 10  # push 10ms of audio per frame


class SyntheticTeacher:
    def __init__(self, ws_url: str, join_token: str, wav_path: Path) -> None:
        self._ws_url = ws_url
        self._token = join_token
        self._wav_path = wav_path
        self._room = rtc.Room()
        self._source: rtc.AudioSource | None = None
        self._rate = 16000
        self._channels = 1
        # Every feedback payload received on the teaching-suggestion topic.
        self.feedback: list[dict] = []

    async def __aenter__(self) -> "SyntheticTeacher":
        await self.connect()
        return self

    async def __aexit__(self, *exc) -> None:
        await self.leave()

    async def connect(self) -> None:
        self._register_data_handler()
        await self._room.connect(self._ws_url, self._token)
        logger.info("Synthetic teacher connected to room as %s", self._room.local_participant.identity)
        await self._publish_mic()

    def _register_data_handler(self) -> None:
        @self._room.on("data_received")
        def _on_data(*args) -> None:
            # SDK 1.x emits a single DataPacket; older SDKs emit (data, participant, kind, topic).
            packet = args[0]
            payload = getattr(packet, "data", None)
            topic = getattr(packet, "topic", None)
            if payload is None:  # legacy positional form
                payload = args[0]
                topic = args[3] if len(args) > 3 else None
            try:
                message = json.loads(bytes(payload).decode("utf-8"))
            except Exception:  # noqa: BLE001 — ignore non-JSON control frames
                return
            if message.get("type") == "teaching_suggestion" or topic == "teaching_suggestion":
                logger.info("Received teaching_suggestion feedback: type=%s", message.get("feedback_type"))
                self.feedback.append(message)

    async def _publish_mic(self) -> None:
        with wave.open(str(self._wav_path), "rb") as w:
            self._rate = w.getframerate()
            self._channels = w.getnchannels()
        self._source = rtc.AudioSource(self._rate, self._channels)
        track = rtc.LocalAudioTrack.create_audio_track("teacher-mic", self._source)
        options = rtc.TrackPublishOptions(source=rtc.TrackSource.SOURCE_MICROPHONE)
        await self._room.local_participant.publish_track(track, options)
        logger.info("Published mic track (%d Hz, %d ch)", self._rate, self._channels)

    async def speak(self, *, repeat: int = 1, realtime: bool = True) -> None:
        """Stream the WAV into the room, optionally repeated to give STT more to chew on."""
        assert self._source is not None, "connect() must run before speak()"
        with wave.open(str(self._wav_path), "rb") as w:
            frames = w.readframes(w.getnframes())
        samples_per_frame = int(self._rate * _FRAME_MS / 1000)
        bytes_per_frame = samples_per_frame * self._channels * 2  # int16
        for _ in range(repeat):
            for offset in range(0, len(frames), bytes_per_frame):
                chunk = frames[offset : offset + bytes_per_frame]
                if len(chunk) < bytes_per_frame:
                    chunk = chunk + b"\x00" * (bytes_per_frame - len(chunk))
                audio_frame = rtc.AudioFrame(
                    data=chunk,
                    sample_rate=self._rate,
                    num_channels=self._channels,
                    samples_per_channel=samples_per_frame,
                )
                await self._source.capture_frame(audio_frame)
                if realtime:
                    await asyncio.sleep(_FRAME_MS / 1000)
            # A short silence between repeats acts as a thought-boundary pause.
            if realtime:
                await asyncio.sleep(1.0)
        logger.info("Finished streaming teacher audio (%d repeat(s))", repeat)

    async def wait_for_feedback(self, timeout_s: float, poll_s: float = 0.5) -> dict | None:
        """Return the first feedback payload received, or None if none within the window."""
        deadline = asyncio.get_event_loop().time() + timeout_s
        while asyncio.get_event_loop().time() < deadline:
            if self.feedback:
                return self.feedback[0]
            await asyncio.sleep(poll_s)
        return None

    async def leave(self) -> None:
        with contextlib.suppress(Exception):
            await self._room.disconnect()
