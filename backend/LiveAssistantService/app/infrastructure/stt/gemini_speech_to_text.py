"""HOSTED STT engine: Gemini's multimodal generateContent, behind the ``SpeechToText`` port.

WHY THIS EXISTS. Groq blocks datacenter/VPN exit IPs and returns 403 for every transcription
request from one. Measured from the same container at the same moment through a ProtonVPN
WireGuard tunnel: Groq /models = 403, Gemini embedContent = 200. When the operator cannot work
without the VPN, an engine on a provider that tolerates the exit IP is the only STT that runs at
all — and this service already holds a working Gemini key for the brain and the drift embedder,
so it adds no new credential.

Secondary benefit: Gemini's audio understanding is multilingual, so leaving STT_LANGUAGE empty
transcribes whatever is spoken. The Whisper engines are pinned to one configured language.

HOW IT DIFFERS FROM A REAL STT. This is a general-purpose LLM being asked to transcribe, not a
dedicated ASR model, and that changes the failure modes rather than removing them:

  * It may answer ABOUT the audio ("There is no speech in this clip") instead of transcribing it.
    Whisper returns "" for silence; an LLM narrates. Handled with an explicit ``<NO_SPEECH>``
    sentinel it is instructed to emit, plus a normalized backstop list — see ``_clean_reply``.
  * It may wrap output in markdown fences or quotes, or prepend "Here is the transcription:".
  * It is instruction-following, so it can be tempted to tidy grammar or complete a half-finished
    sentence. The prompt forbids this explicitly, because for boundary detection a *verbatim*
    transcript matters more than a readable one.

⚠ MEASURED: THIS IS TOO SLOW FOR LIVE USE. Do not reach for it as a general alternative to Groq.
On a 10s window against gemini-flash-latest: 14.85s and 19.96s (median 17.4s) = **0.5-0.67x
realtime**, plus one 503 "model is currently experiencing high load" in three attempts. Groq
transcribes an 11s window in ~2s (≈5x realtime), so this is roughly 8-10x slower AND below 1.0x
realtime — meaning it cannot keep up with a live lecture at all. It falls progressively further
behind the longer the class runs, and no amount of buffering fixes an engine slower than its input.

It also HALLUCINATES ON NEAR-SILENCE: 3 of 4 windows of pure digital silence returned words
("And so I think", "So", "So") instead of the ``<NO_SPEECH>`` sentinel. In practice
``StreamingTranscriber``'s energy gate means an all-silent window rarely reaches an engine at all
(leading silence is skipped and a window only opens on a speech frame), so this matters less than
the raw rate suggests — but quiet windows remain a risk, and Whisper is not immune either.

WHAT THIS IS ACTUALLY FOR: a last-resort escape hatch when Groq is hard-blocked and the operator
cannot change the network path — some transcription beats none, at the cost of a badly lagging
assistant. THE REAL FIX for a 403 is the route (switch VPN server, or exclude api.groq.com from
the tunnel), not this engine. ``tests/test_gemini_stt_real.py`` re-measures both on your own clip.

NO FALLBACK BY DESIGN, matching the Groq engine: a failure raises ``GeminiSpeechToTextError``
rather than silently degrading to an engine 20x slower.
"""

from __future__ import annotations

import base64
import logging
import string

import httpx
import numpy as np

from app.infrastructure.config.settings import Settings
from app.infrastructure.stt.audio_analysis import DEFAULT_SILENCE_RMS
from app.infrastructure.stt.streaming_transcriber import StreamingTranscriber
from app.infrastructure.stt.wav_encoding import to_wav_bytes
from app.observability import metrics

logger = logging.getLogger("liveassistant.stt")

# The model is told to emit exactly this when it hears no intelligible speech. A sentinel is far
# more robust than trying to enumerate every phrasing an LLM might invent for "there was nothing
# here" — and unlike a natural-language refusal it can never collide with real transcript text.
_NO_SPEECH_SENTINEL = "<NO_SPEECH>"

# Backstop for when the model narrates instead of using the sentinel. Matched against the FULLY
# NORMALIZED reply (lowercased, punctuation and whitespace stripped) and only as an EXACT whole-
# reply match — never a substring — so a teacher who genuinely says "there is no audio here" mid
# lecture still gets transcribed.
_NO_SPEECH_EQUIVALENTS = frozenset(
    {
        "nospeech",
        "nospeechdetected",
        "nospeechdetectedintheaudio",
        "noaudio",
        "noaudiodetected",
        "silence",
        "silent",
        "theaudioissilent",
        "thereisnospeech",
        "thereisnospeechinthisaudio",
        "thereisnospeechintheaudio",
        "thereisnoaudiblespeech",
        "theaudiocontainsnospeech",
        "theaudiocontainsnointelligiblespeech",
        "inaudible",
        "unintelligible",
        "icannottranscribethisaudio",
        "icannotdeterminewhatissaid",
        "na",  # a bare "N/A" normalizes to this
        "none",
        "empty",
    }
)

# Preambles an instruction-following model sometimes prepends despite being told not to. Stripped
# case-insensitively from the front of the reply, before the no-speech checks.
_PREAMBLES = (
    "here is the transcription:",
    "here is the transcript:",
    "here's the transcription:",
    "here's the transcript:",
    "transcription:",
    "transcript:",
    "the transcription is:",
    "the audio says:",
)


def _normalize(text: str) -> str:
    """Lowercase and drop everything that is not a letter or digit.

    Used only for comparing a whole reply against ``_NO_SPEECH_EQUIVALENTS``, so punctuation and
    spacing differences ("No speech detected." vs "no speech detected") collapse together.
    """
    return "".join(ch for ch in text.lower() if ch not in string.punctuation and not ch.isspace())


def _strip_wrapping(text: str) -> str:
    """Remove markdown fences and matched surrounding quotes the model may add."""
    cleaned = text.strip()
    if cleaned.startswith("```"):
        # Drop the opening fence (with an optional language tag) and the closing one.
        newline = cleaned.find("\n")
        cleaned = cleaned[newline + 1 :] if newline != -1 else cleaned[3:]
        if cleaned.rstrip().endswith("```"):
            cleaned = cleaned.rstrip()[:-3]
        cleaned = cleaned.strip()
    for quote in ('"', "'", "“", "‘"):
        closing = {'"': '"', "'": "'", "“": "”", "‘": "’"}[quote]
        if len(cleaned) >= 2 and cleaned.startswith(quote) and cleaned.endswith(closing):
            cleaned = cleaned[1:-1].strip()
            break
    return cleaned


def _clean_reply(raw: str) -> str:
    """Turn a raw model reply into transcript text, or '' when it reported no speech.

    Returning '' is what makes this engine behave like an ASR model to the rest of the pipeline:
    ``StreamingTranscriber`` drops empty windows, so a narrated "there was no speech" never
    reaches the boundary detector as if the teacher had said it.
    """
    cleaned = _strip_wrapping(raw)

    lowered = cleaned.lower()
    for preamble in _PREAMBLES:
        if lowered.startswith(preamble):
            cleaned = _strip_wrapping(cleaned[len(preamble) :])
            break

    if _NO_SPEECH_SENTINEL.lower() in cleaned.lower():
        return ""
    if _normalize(cleaned) in _NO_SPEECH_EQUIVALENTS:
        return ""
    return cleaned.strip()


def _build_prompt(language: str, vocabulary: str) -> str:
    """The transcription instruction. Kept in code so it is easy to tune alongside the filters."""
    if language:
        language_rule = (
            f"- The audio is expected to be in {language}. Transcribe it in that language; "
            "do NOT translate it."
        )
    else:
        # Empty STT_LANGUAGE is the multilingual mode — the reason this engine helps the Arabic goal.
        language_rule = (
            "- Transcribe in whatever language is actually spoken. Do NOT translate to English."
        )

    rules = [
        "You are a speech-to-text engine. Transcribe the audio VERBATIM.",
        "",
        "Rules:",
        "- Output ONLY the transcribed words. No preamble, no commentary, no explanation, no "
        "quotes, no markdown.",
        language_rule,
        "- Transcribe exactly what is said, including false starts and repetitions. Do NOT "
        "summarize, paraphrase, correct grammar, or complete an unfinished sentence.",
        "- This is one short window from the middle of a continuous lecture, so it may begin or "
        "end mid-sentence. That is expected — do not try to make it a complete thought.",
        f"- If there is no intelligible speech, output exactly {_NO_SPEECH_SENTINEL} and nothing "
        "else. Never describe the audio.",
    ]
    if vocabulary:
        rules.append(
            f"- Domain vocabulary that may appear, spelled correctly: {vocabulary}"
        )
    return "\n".join(rules)


class GeminiSpeechToTextError(RuntimeError):
    """Raised when the Gemini API cannot produce a transcription (transport/HTTP/auth)."""


class GeminiSpeechToText(StreamingTranscriber):
    """Gemini-hosted multimodal implementation of the streaming ``SpeechToText`` port."""

    def __init__(
        self,
        settings: Settings,
        *,
        silence_rms_threshold: float = DEFAULT_SILENCE_RMS,
    ) -> None:
        super().__init__(settings, silence_rms_threshold=silence_rms_threshold)
        self._base_url = settings.gemini_base_url.rstrip("/")
        self._model = settings.gemini_stt_model
        self._api_key = settings.gemini_api_key
        self._timeout = settings.gemini_stt_timeout_seconds
        self._prompt = _build_prompt(
            settings.stt_language.strip(), settings.stt_initial_prompt.strip()
        )
        self._debug_log_text = settings.stt_debug_log_text
        self._sample_rate = settings.target_sample_rate
        # Transcription is not a creative task: temperature 0 keeps it from paraphrasing, and
        # thinkingLevel low keeps latency down (there is nothing here worth reasoning about).
        self._thinking_level = settings.gemini_stt_thinking_level.strip()
        if not self._api_key:
            logger.warning(
                "GEMINI_API_KEY is empty; Gemini transcription will fail until it is set (put it "
                "in LiveAssistantService/.env)."
            )

    async def _transcribe_window(self, samples: np.ndarray, sample_rate: int) -> str:
        url = f"{self._base_url}/models/{self._model}:generateContent"
        audio_b64 = base64.b64encode(to_wav_bytes(samples, sample_rate)).decode("ascii")

        generation_config: dict = {"temperature": 0.0}
        if self._thinking_level:
            generation_config["thinkingConfig"] = {"thinkingLevel": self._thinking_level}

        payload = {
            "contents": [
                {
                    "role": "user",
                    "parts": [
                        {"text": self._prompt},
                        {"inlineData": {"mimeType": "audio/wav", "data": audio_b64}},
                    ],
                }
            ],
            "generationConfig": generation_config,
        }
        # Key travels in a header (not the URL), so it never lands in request logs.
        headers = {"x-goog-api-key": self._api_key, "Content-Type": "application/json"}

        with metrics.stt_segment_timer():
            try:
                async with httpx.AsyncClient(timeout=self._timeout) as client:
                    response = await client.post(url, json=payload, headers=headers)
            except httpx.RequestError as exc:
                raise GeminiSpeechToTextError(
                    f"Could not reach the Gemini API at {self._base_url}. Check network/"
                    f"GEMINI_BASE_URL. Original error: {exc}"
                ) from exc

        if response.status_code in (401, 403) or (
            response.status_code == 400 and "API_KEY" in response.text
        ):
            raise GeminiSpeechToTextError(
                "Gemini rejected the transcription request (check GEMINI_API_KEY and that the "
                f"free tier is active). HTTP {response.status_code}: {response.text[:300]}"
            )
        if response.status_code == 429:
            raise GeminiSpeechToTextError(
                "Gemini rate limit hit (429). Audio requests are token-expensive, so a lecture "
                f"can exhaust a free-tier quota faster than text does. Detail: {response.text[:200]}"
            )
        if response.status_code >= 400:
            raise GeminiSpeechToTextError(
                f"Gemini transcription failed with HTTP {response.status_code}: "
                f"{response.text[:300]}"
            )

        text = _clean_reply(_extract_text(response.json()))
        if self._debug_log_text:
            # DEV ONLY (STT_DEBUG_LOG_TEXT=true): shows exactly what the model returned for this
            # window, so an empty/garbled result can be told apart from a silent mic.
            logger.info(
                "stt_debug",
                extra={
                    "duration_s": round(float(samples.size) / float(self._sample_rate), 2),
                    "chars": len(text),
                    "text": text,
                },
            )
        return text


def _extract_text(data: dict) -> str:
    """Pull the reply text out of a generateContent response; '' if blocked/empty.

    A blocked or truncated window degrades to no transcript for that window rather than raising:
    losing one window is survivable, whereas ending the session is not.
    """
    candidates = data.get("candidates") or []
    if not candidates:
        logger.warning("Gemini returned no candidates for an audio window (possibly blocked).")
        return ""
    candidate = candidates[0] or {}
    if candidate.get("finishReason") == "MAX_TOKENS":
        # Thinking tokens share the output budget, so a starved cap truncates the transcript.
        logger.warning("Gemini hit MAX_TOKENS transcribing a window; transcript may be truncated.")
    parts = (candidate.get("content") or {}).get("parts") or []
    return "".join(str(part.get("text", "")) for part in parts).strip()
