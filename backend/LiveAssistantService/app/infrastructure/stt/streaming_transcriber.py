"""The engine-agnostic half of streaming STT.

Whisper-style models are not natively streaming, so this implements *pseudo-streaming*: accumulate
incoming frames into a per-utterance window, finalize the window when a silence gap or the length
cap says the utterance is over, and hand exactly that window to an engine.

Everything here is independent of WHICH engine transcribes — buffering, energy-based pause
detection, the length cap, timestamps, interim gating. Subclasses implement one method:

    async def _transcribe_window(samples: np.ndarray, sample_rate: int) -> str

``FasterWhisperSpeechToText`` runs a local CTranslate2 model there; ``GroqSpeechToText`` POSTs the
window to a hosted API. Because the seam takes a finished window and returns text, a file-based
remote API fits it exactly as well as a local model — no streaming protocol is required either way.

Pause detection is a simple energy gate on the audio (see ``audio_analysis``), NOT the engine's own
VAD, which is why it works identically for a remote engine that exposes no VAD controls.
"""

from __future__ import annotations

import logging
from abc import abstractmethod
from collections.abc import AsyncIterator

import numpy as np

from app.application.ports.speech_to_text import SpeechToText
from app.domain.audio.audio_frame import AudioFrame
from app.domain.transcript.transcript_segment import TranscriptSegment
from app.infrastructure.config.settings import Settings
from app.infrastructure.stt.audio_analysis import (
    DEFAULT_SILENCE_RMS,
    pcm16_to_float32,
    rms,
)

logger = logging.getLogger("liveassistant.stt")


class StreamingTranscriber(SpeechToText):
    """Windowing/pause state machine shared by every STT engine."""

    def __init__(
        self,
        settings: Settings,
        *,
        silence_rms_threshold: float = DEFAULT_SILENCE_RMS,
    ) -> None:
        self._settings = settings
        self._silence_threshold = silence_rms_threshold

    async def warmup(self) -> None:
        """Prepare the engine (load weights, open a connection). No-op by default."""
        return

    @abstractmethod
    async def _transcribe_window(self, samples: np.ndarray, sample_rate: int) -> str:
        """Transcribe one finished mono float32 window; return stripped text ('' if nothing)."""
        raise NotImplementedError

    async def transcribe(
        self, frames: AsyncIterator[AudioFrame]
    ) -> AsyncIterator[TranscriptSegment]:
        settings = self._settings
        sample_rate = settings.target_sample_rate
        pause_seconds = settings.stt_pause_seconds
        chunk_samples = max(1, int(settings.stt_chunk_seconds * sample_rate))
        emit_interim = settings.stt_emit_interim
        # Bounds the re-transcribed window so a speaker who never pauses cannot grow it without
        # limit (transcription cost is superlinear in window length).
        max_window_samples = max(1, int(settings.stt_max_window_seconds * sample_rate))

        window: list[np.ndarray] = []  # float32 chunks of the current utterance
        buffered = 0  # samples in `window`
        emitted_at = 0  # `buffered` value at the last interim emit
        start_ms: int | None = None  # utterance start; None while between utterances
        last_end_ms = 0
        trailing_silence = 0.0  # seconds of unbroken trailing silence in the utterance

        # DIAGNOSTIC (temporary): roll up the incoming audio LEVEL and log it ~once/sec so we can
        # tell a truly-silent mic apart from a threshold/format issue. peak_rms well below
        # `threshold` => no audible signal is reaching the agent. Remove once confirmed.
        _probe_secs = 0.0
        _probe_peak = 0.0
        _probe_speech = 0
        _probe_total = 0

        async for frame in frames:
            samples = pcm16_to_float32(frame.pcm)
            level = rms(samples)
            silent = level < self._silence_threshold

            _probe_secs += frame.duration_seconds
            _probe_peak = max(_probe_peak, level)
            _probe_total += 1
            if not silent:
                _probe_speech += 1
            if _probe_secs >= 1.0:
                logger.info(
                    "audio_level_probe",
                    extra={
                        "peak_rms": round(_probe_peak, 5),
                        "threshold": round(self._silence_threshold, 5),
                        "speech_frames": _probe_speech,
                        "total_frames": _probe_total,
                    },
                )
                _probe_secs = 0.0
                _probe_peak = 0.0
                _probe_speech = 0
                _probe_total = 0

            # Drop leading silence so an utterance starts on the first speech frame.
            if start_ms is None:
                if silent:
                    continue
                start_ms = int(round(frame.timestamp * 1000))

            window.append(samples)
            buffered += samples.shape[0]
            trailing_silence = trailing_silence + frame.duration_seconds if silent else 0.0
            last_end_ms = int(round((frame.timestamp + frame.duration_seconds) * 1000))

            # A pause closes the utterance, OR the window hits its cap. Both finalize; only the
            # pause sets followed_by_pause (the cap is a safety net, not a thought break, so it
            # must not masquerade as one to the boundary detector).
            hit_pause = trailing_silence >= pause_seconds
            hit_cap = buffered >= max_window_samples
            if hit_pause or hit_cap:
                if hit_cap and not hit_pause:
                    # Counts only — never the text. Frequent hits mean the cap is doing real work
                    # and the speaker rarely pauses; useful signal when tuning.
                    logger.info(
                        "stt_window_capped", extra={"seconds": round(buffered / sample_rate, 1)}
                    )
                text = await self._transcribe_window(np.concatenate(window), sample_rate)
                if text:
                    yield TranscriptSegment(
                        text=text,
                        start_ms=start_ms,
                        end_ms=last_end_ms,
                        is_final=True,
                        followed_by_pause=hit_pause,
                    )
                window.clear()
                buffered = emitted_at = 0
                start_ms = None
                trailing_silence = 0.0
                continue

            # Otherwise emit an interim segment every STT_CHUNK_SECONDS of new audio. Off by
            # default: re-transcribing the whole utterance-so-far to produce a segment that no
            # consumer reads is pure CPU burn that delays the final. See stt_emit_interim.
            if emit_interim and buffered - emitted_at >= chunk_samples:
                text = await self._transcribe_window(np.concatenate(window), sample_rate)
                emitted_at = buffered
                if text:
                    yield TranscriptSegment(
                        text=text,
                        start_ms=start_ms,
                        end_ms=last_end_ms,
                        is_final=False,
                        followed_by_pause=False,
                    )

        # Stream ended mid-utterance -> finalize what we have.
        if window and start_ms is not None:
            text = await self._transcribe_window(np.concatenate(window), sample_rate)
            if text:
                yield TranscriptSegment(
                    text=text,
                    start_ms=start_ms,
                    end_ms=last_end_ms,
                    is_final=True,
                    followed_by_pause=trailing_silence >= pause_seconds,
                )
