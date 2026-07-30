"""HOSTED STT engine: Groq's Whisper API, behind the ``SpeechToText`` port.

Only the engine call lives here — the windowing/pause state machine is shared, in
``StreamingTranscriber``. Groq's endpoint is file-based (POST an audio clip, get text), which fits
that seam exactly: the state machine already hands over a *finished* window, so no streaming
protocol is needed.

WHY THIS EXISTS. Local faster-whisper cannot keep up with live speech on a CPU laptop. Measured
on the same 11s clip: local small.en beam=5 = 0.2x realtime, tiny.en beam=1 = 1.8x (worst
accuracy); Groq whisper-large-v3-turbo = ~4.5x realtime WITH large-v3 accuracy. Roughly 20x
faster than the local config it replaces, and more accurate rather than less.

LATENCY IS DOMINATED BY UPLOAD, not compute — turbo and large-v3 both measured ~2.4s for an 11s
window (348KB of WAV). So keeping STT_MAX_WINDOW_SECONDS small helps here for a different reason
than it helps locally: it shrinks the upload, not the compute.

NO FALLBACK BY DESIGN. A failure raises ``GroqSpeechToTextError`` and the session surfaces it,
rather than silently switching to an engine 20x slower — a half-speed mystery mode is harder to
diagnose than a loud stop. HTTP 403 is called out separately because its overwhelmingly likely
cause here is a VPN/datacenter exit IP on Groq's blocklist, not anything about the request.
"""

from __future__ import annotations

import logging

import httpx
import numpy as np

from app.infrastructure.config.settings import Settings
from app.infrastructure.stt.audio_analysis import DEFAULT_SILENCE_RMS
from app.infrastructure.stt.streaming_transcriber import StreamingTranscriber
from app.infrastructure.stt.wav_encoding import to_wav_bytes
from app.observability import metrics

logger = logging.getLogger("liveassistant.stt")


class GroqSpeechToTextError(RuntimeError):
    """Raised when the Groq transcription API cannot produce text (transport/HTTP/auth)."""


class GroqSpeechToText(StreamingTranscriber):
    """Groq-hosted Whisper implementation of the streaming ``SpeechToText`` port."""

    def __init__(
        self,
        settings: Settings,
        *,
        silence_rms_threshold: float = DEFAULT_SILENCE_RMS,
    ) -> None:
        super().__init__(settings, silence_rms_threshold=silence_rms_threshold)
        self._base_url = settings.groq_base_url.rstrip("/")
        self._model = settings.groq_stt_model
        self._api_key = settings.groq_api_key
        self._timeout = settings.groq_timeout_seconds
        self._language = settings.stt_language.strip()
        self._prompt = settings.stt_initial_prompt.strip()
        self._debug_log_text = settings.stt_debug_log_text
        self._sample_rate = settings.target_sample_rate
        if not self._api_key:
            logger.warning(
                "GROQ_API_KEY is empty; Groq transcription will fail until it is set (put it "
                "in LiveAssistantService/.env)."
            )

    async def _transcribe_window(self, samples: np.ndarray, sample_rate: int) -> str:
        url = f"{self._base_url}/audio/transcriptions"
        files = {"file": ("window.wav", to_wav_bytes(samples, sample_rate), "audio/wav")}
        data: dict[str, str] = {"model": self._model, "response_format": "json"}
        if self._language:
            data["language"] = self._language
        # Same knob as the local engine: seeds the decoder with domain vocabulary.
        if self._prompt:
            data["prompt"] = self._prompt
        headers = {"Authorization": f"Bearer {self._api_key}"}

        with metrics.stt_segment_timer():
            try:
                async with httpx.AsyncClient(timeout=self._timeout) as client:
                    response = await client.post(url, files=files, data=data, headers=headers)
            except httpx.RequestError as exc:
                raise GroqSpeechToTextError(
                    f"Could not reach the Groq API at {self._base_url}. Check network/"
                    f"GROQ_BASE_URL. Original error: {exc}"
                ) from exc

        if response.status_code == 403:
            # Distinct on purpose: Groq blocks known VPN/datacenter exit IPs, and the failure is
            # about WHERE the request came from, not what was in it. Without this the operator
            # sees a generic HTTP error and looks at the audio or the key instead of the route.
            raise GroqSpeechToTextError(
                "Groq returned 403 Forbidden. This is almost always a blocked exit IP — if a VPN "
                "is active, switch to a different server or exclude api.groq.com from it. "
                "(Verify with: curl -s -o /dev/null -w '%{http_code}' https://api.groq.com)"
            )
        if response.status_code == 401:
            raise GroqSpeechToTextError(
                "Groq rejected the API key (401). Check GROQ_API_KEY in "
                "LiveAssistantService/.env."
            )
        if response.status_code == 429:
            raise GroqSpeechToTextError(
                "Groq rate limit hit (429). The free tier caps audio-seconds per hour; wait, or "
                f"reduce load. Detail: {response.text[:200]}"
            )
        if response.status_code >= 400:
            raise GroqSpeechToTextError(
                f"Groq transcription failed with HTTP {response.status_code}: "
                f"{response.text[:300]}"
            )

        text = str(response.json().get("text", "")).strip()
        if self._debug_log_text:
            # DEV ONLY (STT_DEBUG_LOG_TEXT=true): shows exactly what Whisper heard for this window,
            # so an empty/garbled result can be told apart from a silent mic. Mirrors the local
            # engine's stt_debug line — without it, switching to STT_PROVIDER=groq silently loses
            # all transcript visibility. Off by default so production stays privacy-clean.
            logger.info(
                "stt_debug",
                extra={
                    "duration_s": round(float(samples.size) / float(self._sample_rate), 2),
                    "chars": len(text),
                    "text": text,
                },
            )
        return text
