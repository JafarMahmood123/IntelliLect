from __future__ import annotations

from typing import Annotated

from fastapi import Depends

from app.application.ports.audio_source import AudioSource
from app.application.ports.brain_client import BrainClient
from app.application.ports.embedding_provider import EmbeddingProvider
from app.application.ports.retrieval_client import RetrievalClient
from app.application.ports.speech_to_text import SpeechToText
from app.application.services.boundary_detector import BoundaryDetector
from app.application.services.idea_evaluator import IdeaEvaluator
from app.infrastructure.audio.fake_audio_source import FakeAudioSource
from app.infrastructure.audio.livekit_audio_source import LiveKitAudioSource
from app.infrastructure.brain.ollama_brain_client import OllamaBrainClient
from app.infrastructure.config.settings import Settings, get_settings
from app.infrastructure.embeddings.ollama_embedding_provider import (
    OllamaEmbeddingProvider,
)
from app.infrastructure.retrieval.knowledge_retrieval_client import (
    KnowledgeRetrievalClient,
)
from app.infrastructure.stt.faster_whisper_speech_to_text import (
    FasterWhisperSpeechToText,
)

# --- Composition root ---------------------------------------------------------
# The only place API code names concrete infrastructure classes. Everything
# downstream depends on the port abstractions in app.application.ports.
#
# No endpoint constructs an AudioSource yet — session lifecycle wiring is LA-6.
# The factories below are the seam the future live-loop orchestrator/session
# endpoint will call, and are exercised today by scripts/capture_check.py.

SettingsDep = Annotated[Settings, Depends(get_settings)]


def build_live_audio_source(settings: Settings) -> AudioSource:
    """The real server-side agent that captures a teacher's audio from a LiveKit room."""
    return LiveKitAudioSource(settings)


def build_fake_audio_source(
    settings: Settings, wav_path: str | None = None
) -> AudioSource:
    """Offline AudioSource (WAV/tone) — no LiveKit. Used by tests and the CLI default."""
    return FakeAudioSource(settings, wav_path)


def build_speech_to_text(settings: Settings) -> SpeechToText:
    """The streaming English STT engine (faster-whisper / CTranslate2).

    Not wired into a live session yet (that is LA-6); registered here so it is
    injectable and replaceable behind the ``SpeechToText`` port. The faster-whisper
    model is loaded lazily on first use (or via ``warmup()``), so constructing this
    is cheap and does not require the engine to be installed at import time.
    """
    return FasterWhisperSpeechToText(settings)


def build_embedding_provider(settings: Settings) -> EmbeddingProvider:
    """Local Ollama embedder used for LA-3 drift measurement.

    Only the deferred ``boundary_check.py --live`` path uses this; the boundary tests
    inject a deterministic fake. No live call happens at construction time.
    """
    return OllamaEmbeddingProvider(settings)


def build_boundary_detector(
    settings: Settings, embedder: EmbeddingProvider
) -> BoundaryDetector:
    """Idea boundary detector (LA-3), configured from settings.

    The ``embedder`` is injected (not constructed here) so callers can supply the
    real Ollama provider or a fake. Not connected to a live session or to retrieval
    yet — this is the seam LA-4 will consume. Application logic stays free of
    ``Settings``: the config primitives are unpacked here.
    """
    return BoundaryDetector(
        embedder,
        drift_threshold=settings.boundary_drift_threshold,
        pause_seconds=settings.boundary_pause_seconds,
        max_seconds=settings.boundary_max_seconds,
        max_tokens=settings.boundary_max_tokens,
        min_tokens=settings.boundary_min_tokens,
    )


def build_retrieval_client(settings: Settings) -> RetrievalClient:
    """KnowledgeService-backed retrieval (POST /api/search). No live call at build."""
    return KnowledgeRetrievalClient(settings)


def build_brain_client(settings: Settings) -> BrainClient:
    """Local Ollama brain that evaluates an idea against retrieved material."""
    return OllamaBrainClient(settings)


def build_idea_evaluator(
    settings: Settings, retrieval: RetrievalClient, brain: BrainClient
) -> IdeaEvaluator:
    """Retrieve + evaluate on an idea boundary (LA-4), configured from settings.

    ``retrieval`` and ``brain`` are injected so callers can supply the real clients or
    deterministic fakes. Not connected to a live session yet (LA-6). Application logic
    stays free of ``Settings``: the config primitives are unpacked here.
    """
    return IdeaEvaluator(
        retrieval,
        brain,
        top_k=settings.retrieval_top_k,
        min_score=settings.retrieval_min_score,
    )
