"""Streaming English STT behind the ``SpeechToText`` port, backed by faster-whisper
(CTranslate2).

faster-whisper is not natively streaming, so this implements *pseudo-streaming*: it
accumulates the incoming normalized frames into a per-utterance buffer and
re-transcribes the growing window every ``STT_CHUNK_SECONDS`` (emitting interim
segments), then finalizes the utterance when a silence gap >= ``STT_PAUSE_SECONDS``
is detected (or the stream ends). Pause detection is a simple energy gate on the
audio (see ``audio_analysis``) — no separate VAD model.

The ``faster_whisper`` import is **lazy** (inside ``load``) so importing this module,
the port, and the DI wiring never requires the engine to be installed — the offline
tests subclass this and override ``_transcribe_window`` to exercise the streaming
state machine with no model. Blocking model calls run off the event loop via
``asyncio.to_thread`` so the async stream is never blocked.
"""

from __future__ import annotations

import asyncio
import logging
import threading
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
from app.observability import metrics

logger = logging.getLogger("liveassistant.stt")


class FasterWhisperSpeechToText(SpeechToText):
    """faster-whisper implementation of the streaming ``SpeechToText`` port."""

    def __init__(
        self,
        settings: Settings,
        *,
        silence_rms_threshold: float = DEFAULT_SILENCE_RMS,
    ) -> None:
        self._settings = settings
        self._silence_threshold = silence_rms_threshold
        self._model = None  # faster_whisper.WhisperModel, loaded lazily and kept warm
        self._load_lock = threading.Lock()

    # -- model lifecycle ------------------------------------------------------
    def load(self) -> None:
        """Load the CTranslate2 model once and keep it warm.

        Blocking; safe to call at startup (a later phase's session bootstrap) or it
        runs lazily on the first transcription inside the worker thread. Idempotent
        and thread-safe.
        """
        if self._model is not None:
            return
        with self._load_lock:
            if self._model is not None:
                return
            # Lazy import: keeps the engine out of the import graph for offline tests.
            from faster_whisper import WhisperModel

            logger.info(
                "Loading STT model %s (device=%s, compute_type=%s)",
                self._settings.stt_model,
                self._settings.stt_device,
                self._settings.stt_compute_type,
            )
            self._model = WhisperModel(
                self._settings.stt_model,
                device=self._settings.stt_device,
                compute_type=self._settings.stt_compute_type,
                # Cap intra-op threads so STT leaves cores free for the LLM's generation
                # (0 => CTranslate2 default = all cores, which starves Ollama). See
                # settings.stt_cpu_threads.
                cpu_threads=self._settings.stt_cpu_threads,
            )

    async def warmup(self) -> None:
        """Async-friendly wrapper around ``load`` for startup wiring (LA-6)."""
        await asyncio.to_thread(self.load)

    # -- streaming transcription ---------------------------------------------
    async def transcribe(
        self, frames: AsyncIterator[AudioFrame]
    ) -> AsyncIterator[TranscriptSegment]:
        settings = self._settings
        sample_rate = settings.target_sample_rate
        pause_seconds = settings.stt_pause_seconds
        chunk_samples = max(1, int(settings.stt_chunk_seconds * sample_rate))

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

            # A pause closes the utterance -> emit the final segment.
            if trailing_silence >= pause_seconds:
                text = await self._transcribe_window(np.concatenate(window), sample_rate)
                if text:
                    yield TranscriptSegment(
                        text=text,
                        start_ms=start_ms,
                        end_ms=last_end_ms,
                        is_final=True,
                        followed_by_pause=True,
                    )
                window.clear()
                buffered = emitted_at = 0
                start_ms = None
                trailing_silence = 0.0
                continue

            # Otherwise emit an interim segment every STT_CHUNK_SECONDS of new audio.
            if buffered - emitted_at >= chunk_samples:
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

    async def _transcribe_window(self, samples: np.ndarray, sample_rate: int) -> str:
        """Transcribe a mono float32 window off the event loop; return stripped text.

        Overridden by the offline tests to drive the streaming state machine without
        loading a model.
        """
        # LA-8: time each STT window (stt_segment_latency + stage=stt). Additive.
        with metrics.stt_segment_timer():
            text = await asyncio.to_thread(self._run_model, samples)
        return text.strip()

    def _run_model(self, samples: np.ndarray) -> str:
        self.load()
        # SDK: WhisperModel.transcribe accepts a float32 mono @16kHz numpy array and
        # returns (segments, info); `segments` is a lazy generator whose iteration
        # runs the actual compute. beam_size=1 (greedy) and condition_on_previous_text
        # =False keep it fast and avoid drift across re-transcribed windows. These
        # kwargs are version-sensitive — verify against the pinned faster-whisper.
        segments, info = self._model.transcribe(
            samples,
            language=self._settings.stt_language,
            beam_size=1,
            vad_filter=False,
            condition_on_previous_text=False,
        )
        seg_list = list(segments)  # materialize: this is what actually runs the compute
        text = " ".join(segment.text.strip() for segment in seg_list)
        # DIAGNOSTIC (temporary): reveal exactly what Whisper heard for this window so we can see
        # whether it's empty, garbled, or fine. Logs transcript text on purpose (dev smoke only) —
        # REMOVE this block once the STT path is confirmed.
        logger.info(
            "stt_debug",
            extra={
                "duration_s": round(float(samples.size) / 16000.0, 2),
                "lang": getattr(info, "language", None),
                "lang_prob": round(float(getattr(info, "language_probability", 0.0) or 0.0), 3),
                "n_segments": len(seg_list),
                "text": text[:200],
            },
        )
        return text
