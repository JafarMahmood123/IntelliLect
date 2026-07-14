"""``AudioSource`` that joins a real LiveKit room as a server-side agent and
captures ONLY the teacher's audio track.

The ``livekit`` (realtime SDK) and ``livekit.api`` (token minting) imports are
**lazy** — done inside methods — so the domain/application layers and the offline
test suite (which uses ``FakeAudioSource``) never require the SDK to be installed.

API-version sensitivity: the LiveKit Python SDK evolves quickly. Every call that
has changed shape across recent versions is marked ``# SDK:`` with the behavior it
implements, so it can be re-verified against the pinned SDK (see pyproject).
"""

from __future__ import annotations

import asyncio
import contextlib
import logging
from collections.abc import AsyncIterator

from app.application.ports.audio_source import AudioSource
from app.domain.audio.audio_frame import AudioFrame
from app.domain.entities.session_context import SessionContext
from app.infrastructure.audio.normalization import normalize_pcm
from app.infrastructure.config.settings import Settings

logger = logging.getLogger("liveassistant.audio.livekit")

# Sentinel pushed onto the frame queue to signal end-of-stream (disconnect / room
# closed) so ``frames()`` stops cleanly.
_STREAM_END = object()


class LiveKitAudioSource(AudioSource):
    """Subscribe to the teacher's audio in a LiveKit room and expose it as frames.

    Flow: ``connect`` mints a join token for ``AGENT_IDENTITY``, joins
    ``session.room_name`` with auto-subscribe OFF (so we never receive student
    tracks), then subscribes only to the teacher's audio publication — waiting for
    the teacher to join if they aren't present yet. A background reader per subscribed
    track pulls native PCM, normalizes it to ``TARGET_SAMPLE_RATE``/mono, and pushes
    ``AudioFrame``s onto an internal queue that ``frames()`` drains.
    """

    def __init__(self, settings: Settings) -> None:
        self._settings = settings
        self._session: SessionContext | None = None
        self._room = None  # rtc.Room, created in connect()
        self._queue: asyncio.Queue = asyncio.Queue()
        self._reader_tasks: set[asyncio.Task] = set()
        self._elapsed_samples = 0  # for monotonic stream-time timestamps
        self._closed = False

    # -- lifecycle ------------------------------------------------------------
    async def connect(self, session: SessionContext) -> None:
        if not self._settings.livekit_configured:
            raise RuntimeError(
                "LiveKit is not configured (LIVEKIT_URL/API_KEY/API_SECRET). "
                "Use FakeAudioSource for offline development."
            )
        # Lazy SDK imports — see module docstring.
        from livekit import api, rtc

        self._session = session
        self._room = rtc.Room()
        self._register_handlers(rtc)

        token = self._mint_token(api, session)
        # SDK: auto_subscribe=False → we opt in per-publication so student tracks are
        # never delivered. RoomOptions field name is version-sensitive.
        options = rtc.RoomOptions(auto_subscribe=False)
        await self._room.connect(self._settings.livekit_url, token, options)
        logger.info(
            "Agent %s joined room %s; watching teacher %s",
            self._settings.agent_identity,
            session.room_name,
            session.teacher_identity,
        )

        # The teacher may already be in the room — subscribe to any audio they have
        # published now. If they join later, the event handlers below catch it.
        self._subscribe_existing_teacher_tracks(rtc)

    async def disconnect(self) -> None:
        self._closed = True
        for task in list(self._reader_tasks):
            task.cancel()
        self._reader_tasks.clear()
        if self._room is not None:
            try:
                # SDK: Room.disconnect() is async in current SDK versions.
                await self._room.disconnect()
            except Exception:  # noqa: BLE001 — teardown must not raise
                logger.exception("Error disconnecting from LiveKit room.")
            self._room = None
        # Unblock any frames() consumer still awaiting the queue.
        self._queue.put_nowait(_STREAM_END)

    async def frames(self) -> AsyncIterator[AudioFrame]:
        while True:
            item = await self._queue.get()
            if item is _STREAM_END:
                return
            yield item

    # -- internals ------------------------------------------------------------
    def _mint_token(self, api, session: SessionContext) -> str:
        """Mint a room-join token for the agent that can subscribe but not publish."""
        # SDK: VideoGrants field names (room_join/can_subscribe/can_publish) and the
        # AccessToken builder chain are version-sensitive.
        grants = api.VideoGrants(
            room_join=True,
            room=session.room_name,
            can_subscribe=True,
            can_publish=False,
            can_publish_data=False,
        )
        return (
            api.AccessToken(
                self._settings.livekit_api_key, self._settings.livekit_api_secret
            )
            .with_identity(self._settings.agent_identity)
            .with_name(self._settings.agent_identity)
            .with_grants(grants)
            .to_jwt()
        )

    def _is_teacher(self, participant) -> bool:
        return (
            self._session is not None
            and participant.identity == self._session.teacher_identity
        )

    @staticmethod
    def _is_audio(publication_or_track, rtc) -> bool:
        # SDK: TrackKind enum is rtc.TrackKind.KIND_AUDIO in current versions.
        return getattr(publication_or_track, "kind", None) == rtc.TrackKind.KIND_AUDIO

    def _register_handlers(self, rtc) -> None:
        room = self._room

        # SDK: event names ("track_published", "track_subscribed",
        # "participant_connected", "disconnected") are stable strings in the SDK's
        # EventEmitter; handler argument order is version-sensitive.
        @room.on("track_published")
        def _on_track_published(publication, participant) -> None:
            if self._is_teacher(participant) and self._is_audio(publication, rtc):
                self._request_subscribe(publication)

        @room.on("track_subscribed")
        def _on_track_subscribed(track, publication, participant) -> None:
            if self._is_teacher(participant) and self._is_audio(track, rtc):
                self._start_reader(track, rtc)

        @room.on("participant_connected")
        def _on_participant_connected(participant) -> None:
            if self._is_teacher(participant):
                logger.info("Teacher %s joined the room.", participant.identity)
                self._subscribe_participant_tracks(participant, rtc)

        @room.on("disconnected")
        def _on_disconnected(*_args) -> None:
            logger.info("LiveKit room disconnected; ending frame stream.")
            self._queue.put_nowait(_STREAM_END)

    def _subscribe_existing_teacher_tracks(self, rtc) -> None:
        # SDK: remote participants live on room.remote_participants (a dict keyed by
        # identity) in current versions; older SDKs used room.participants.
        participants = getattr(self._room, "remote_participants", None)
        if participants is None:
            participants = getattr(self._room, "participants", {})
        for participant in participants.values():
            if self._is_teacher(participant):
                self._subscribe_participant_tracks(participant, rtc)

    def _subscribe_participant_tracks(self, participant, rtc) -> None:
        # SDK: publications live on participant.track_publications (dict) in current
        # versions; the audio ones are opted into explicitly.
        publications = getattr(participant, "track_publications", {})
        for publication in publications.values():
            if self._is_audio(publication, rtc):
                self._request_subscribe(publication)

    def _request_subscribe(self, publication) -> None:
        try:
            # SDK: RemoteTrackPublication.set_subscribed(True) opts this single track
            # in. If already subscribed, the SDK delivers track_subscribed anyway.
            publication.set_subscribed(True)
        except Exception:  # noqa: BLE001
            logger.exception("Failed to subscribe to teacher audio publication.")

    def _start_reader(self, track, rtc) -> None:
        if self._closed:
            return
        task = asyncio.create_task(self._read_track(track, rtc))
        self._reader_tasks.add(task)
        task.add_done_callback(self._reader_tasks.discard)

    async def _read_track(self, track, rtc) -> None:
        """Drain one audio track: native PCM -> normalized AudioFrame -> queue."""
        target_rate = self._settings.target_sample_rate
        target_channels = self._settings.target_channels
        # SDK: AudioStream can resample natively via (sample_rate=, num_channels=),
        # but we take native frames and normalize with numpy so the format contract
        # is owned here (and unit-tested) rather than by the SDK.
        audio_stream = rtc.AudioStream(track)
        try:
            async for event in audio_stream:
                # SDK: iteration yields AudioFrameEvent(.frame) in current versions;
                # some older versions yielded the AudioFrame directly.
                frame = getattr(event, "frame", event)
                normalized = normalize_pcm(
                    bytes(frame.data),
                    frame.sample_rate,
                    frame.num_channels,
                    target_rate,
                    target_channels,
                )
                if not normalized:
                    continue
                out = AudioFrame(
                    pcm=normalized,
                    sample_rate=target_rate,
                    channels=target_channels,
                    timestamp=self._elapsed_samples / target_rate,
                )
                self._elapsed_samples += out.num_samples
                await self._queue.put(out)
        except asyncio.CancelledError:
            raise
        except Exception:  # noqa: BLE001
            logger.exception("Teacher audio reader stopped on error.")
        finally:
            # SDK: AudioStream.aclose() releases the native stream in current versions.
            aclose = getattr(audio_stream, "aclose", None)
            if aclose is not None:
                with contextlib.suppress(Exception):  # best-effort cleanup
                    await aclose()
