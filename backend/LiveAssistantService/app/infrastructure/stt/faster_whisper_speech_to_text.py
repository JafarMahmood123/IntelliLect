"""LOCAL STT engine: faster-whisper (CTranslate2), behind the ``SpeechToText`` port.

Only the engine call lives here. The windowing/pause state machine that decides WHEN a window is
finished is shared with every other engine in ``StreamingTranscriber`` — this class implements
just ``_transcribe_window``.

The ``faster_whisper`` import is **lazy** (inside ``load``) so importing this module, the port, and
the DI wiring never requires the engine to be installed — the offline tests subclass this and
override ``_transcribe_window`` to exercise the streaming state machine with no model. Blocking
model calls run off the event loop via ``asyncio.to_thread`` so the async stream is never blocked.

PERFORMANCE (measured 2026-07-28 on an 8-core CPU laptop, 11s clip): small.en beam=5 → 0.2x
realtime, small.en beam=1 → 0.4x, base.en beam=1 → 0.7x, tiny.en beam=1 → 1.8x. Only tiny.en keeps
up with live speech, and its accuracy is the worst of the four. Cost scales with the NUMBER of VAD
speech chunks in a window (each padded to Whisper's 30s frame), not raw duration — which is why a
long unbroken window is disproportionately expensive. For live sessions prefer STT_PROVIDER=groq;
this engine remains the offline/no-network option.
"""

from __future__ import annotations

import asyncio
import logging
import threading

import numpy as np

from app.infrastructure.config.settings import Settings
from app.infrastructure.stt.audio_analysis import DEFAULT_SILENCE_RMS
from app.infrastructure.stt.streaming_transcriber import StreamingTranscriber
from app.observability import metrics

logger = logging.getLogger("liveassistant.stt")


class FasterWhisperSpeechToText(StreamingTranscriber):
    """faster-whisper implementation of the streaming ``SpeechToText`` port."""

    def __init__(
        self,
        settings: Settings,
        *,
        silence_rms_threshold: float = DEFAULT_SILENCE_RMS,
    ) -> None:
        super().__init__(settings, silence_rms_threshold=silence_rms_threshold)
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
        # VAD filtering drops non-speech regions before the model sees them, which is the main
        # defence against Whisper's silence hallucinations ("okay okay okay", ". . ."). condition_
        # _on_previous_text stays False so a repeated window can't seed a repetition loop.
        settings = self._settings
        vad_filter = settings.stt_vad_filter
        vad_parameters = (
            {
                "min_silence_duration_ms": settings.stt_vad_min_silence_ms,
                # Keep a little audio either side of each detected speech chunk so word edges are
                # not clipped off before the model ever sees them.
                "speech_pad_ms": settings.stt_vad_speech_pad_ms,
            }
            if vad_filter
            else None
        )
        segments, info = self._model.transcribe(
            samples,
            language=settings.stt_language,
            beam_size=settings.stt_beam_size,
            vad_filter=vad_filter,
            vad_parameters=vad_parameters,
            condition_on_previous_text=False,
            initial_prompt=settings.stt_initial_prompt or None,
        )
        seg_list = list(segments)  # materialize: this is what actually runs the compute
        text = " ".join(segment.text.strip() for segment in seg_list)
        if settings.stt_debug_log_text:
            # DEV ONLY (STT_DEBUG_LOG_TEXT=true): shows exactly what Whisper heard for this window,
            # so an empty/garbled result can be told apart from a silent mic. This is the ONE place
            # the service logs transcript content — off by default so production stays privacy-clean.
            logger.info(
                "stt_debug",
                extra={
                    "duration_s": round(
                        float(samples.size) / float(settings.target_sample_rate), 2
                    ),
                    "lang": getattr(info, "language", None),
                    "lang_prob": round(
                        float(getattr(info, "language_probability", 0.0) or 0.0), 3
                    ),
                    "n_segments": len(seg_list),
                    "text": text[:200],
                },
            )
        return text
