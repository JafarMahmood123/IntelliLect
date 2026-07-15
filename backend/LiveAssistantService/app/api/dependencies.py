from __future__ import annotations

import logging
from typing import Annotated

from fastapi import Depends, Header, HTTPException, Request, status

from app.application.ports.agent_data_channel import AgentDataChannel
from app.application.ports.audio_source import AudioSource
from app.application.ports.brain_client import BrainClient
from app.application.ports.embedding_provider import EmbeddingProvider
from app.application.ports.feedback_sink import FeedbackSink
from app.application.ports.retrieval_client import RetrievalClient
from app.application.ports.speech_to_text import SpeechToText
from app.application.ports.transcript_repository import TranscriptRepository
from app.application.services.boundary_detector import BoundaryDetector
from app.application.services.feedback_dispatcher import FeedbackDispatcher
from app.application.services.feedback_pacer import FeedbackPacer
from app.application.services.idea_evaluator import IdeaEvaluator
from app.application.services.session_manager import (
    SessionManager,
    SessionPipelineFactory,
)
from app.application.services.session_pipeline import SessionPipeline
from app.application.services.transcript_recorder import TranscriptRecorder
from app.domain.entities.session_context import SessionContext
from app.infrastructure.audio.fake_audio_source import FakeAudioSource
from app.infrastructure.audio.livekit_audio_source import LiveKitAudioSource
from app.infrastructure.brain.ollama_brain_client import OllamaBrainClient
from app.infrastructure.config.settings import Settings, get_settings
from app.infrastructure.embeddings.ollama_embedding_provider import (
    OllamaEmbeddingProvider,
)
from app.infrastructure.feedback.livekit_feedback_sink import LiveKitFeedbackSink
from app.infrastructure.persistence.database import get_session_factory
from app.infrastructure.persistence.in_memory_transcript_repository import (
    InMemoryTranscriptRepository,
)
from app.infrastructure.persistence.sqlalchemy_transcript_repository import (
    SqlAlchemyTranscriptRepository,
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

logger = logging.getLogger("liveassistant.di")

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


def build_feedback_sink(settings: Settings, channel: AgentDataChannel) -> FeedbackSink:
    """Teacher-only feedback delivery (LA-5) over the agent's room connection.

    ``channel`` is the connected agent (``LiveKitAudioSource`` implements
    ``AgentDataChannel``) — injected because the room connection is per-session and
    provided by the live loop (LA-6), not constructed here. Publishes to the teacher
    identity ONLY; there is no broadcast path.
    """
    return LiveKitFeedbackSink(channel, message_version=settings.feedback_message_version)


def build_feedback_dispatcher(sink: FeedbackSink) -> FeedbackDispatcher:
    """Connector: route evaluation outcomes with feedback to the teacher-only sink.

    ``sink`` is injected so callers can supply the real ``LiveKitFeedbackSink`` or a
    fake."""
    return FeedbackDispatcher(sink)


# --- Pacing, safety & suppression (LA-7) --------------------------------------
def build_feedback_pacer(settings: Settings) -> FeedbackPacer:
    """The pacing gate, configured from settings. Uses a real monotonic clock in prod;
    tests inject a fake clock. State is per-session (keyed by session_id)."""
    return FeedbackPacer(
        min_interval_sec=settings.feedback_min_interval_sec,
        confidence_min=settings.feedback_confidence_min,
        dedup_window_sec=settings.feedback_dedup_window_sec,
        dedup_similarity=settings.feedback_dedup_similarity,
        max_per_session=settings.feedback_max_per_session,
    )


# --- Transcript persistence (S-0) ---------------------------------------------
def build_transcript_repository(settings: Settings) -> TranscriptRepository:
    """The durable transcript store, or an in-memory fallback when no DB is configured.

    With ``TRANSCRIPT_DB_URL`` set, transcripts persist in Postgres (survive a crash);
    without it the service still runs fully offline against a non-durable in-memory
    store. Built ONCE at startup and shared by every session pipeline AND the internal
    transcript endpoint, so the endpoint reads exactly what the pipelines wrote.
    """
    if settings.transcript_db_url:
        return SqlAlchemyTranscriptRepository(get_session_factory())
    logger.warning(
        "TRANSCRIPT_DB_URL is not set — using a non-durable in-memory transcript store."
    )
    return InMemoryTranscriptRepository()


def get_transcript_repository(request: Request) -> TranscriptRepository:
    repo: TranscriptRepository | None = getattr(
        request.app.state, "transcript_repository", None
    )
    if repo is None:
        raise HTTPException(
            status_code=status.HTTP_503_SERVICE_UNAVAILABLE,
            detail="Transcript store is not running.",
        )
    return repo


TranscriptRepositoryDep = Annotated[
    TranscriptRepository, Depends(get_transcript_repository)
]


# --- Session lifecycle (LA-6) -------------------------------------------------
def build_session_pipeline_factory(
    settings: Settings, transcript_repository: TranscriptRepository
) -> SessionPipelineFactory:
    """Factory that assembles the full per-session loop from the phase components.

    Each session gets its OWN LiveKit agent (its own room connection + boundary
    buffer). The agent is used BOTH as the capture ``AudioSource`` and — because it
    implements ``AgentDataChannel`` — as the feedback channel, so feedback flows back
    over the same single connection (no second connection). Stateless HTTP clients
    (embedder / retrieval / brain) are constructed per session too; they hold no
    per-session state.

    One shared ``FeedbackPacer`` (LA-7) serves all sessions, keyed by session_id — the
    pipeline resets its session's pacing state on teardown. The shared
    ``transcript_repository`` (S-0) is durable/process-wide; each session gets its own
    ``TranscriptRecorder`` (per-session writer state) over it.
    """
    pacer = build_feedback_pacer(settings)

    def factory(session: SessionContext) -> SessionPipeline:
        agent = LiveKitAudioSource(settings)  # AudioSource + AgentDataChannel
        stt = FasterWhisperSpeechToText(settings)
        boundary = build_boundary_detector(settings, OllamaEmbeddingProvider(settings))
        evaluator = build_idea_evaluator(
            settings, KnowledgeRetrievalClient(settings), OllamaBrainClient(settings)
        )
        dispatcher = build_feedback_dispatcher(build_feedback_sink(settings, agent))
        recorder = TranscriptRecorder(
            transcript_repository,
            session.session_id,
            session.classroom_id,
            batch=settings.transcript_persist_batch,
        )
        return SessionPipeline(
            session, agent, stt, boundary, evaluator, pacer, dispatcher, recorder
        )

    return factory


def build_session_manager(
    settings: Settings, transcript_repository: TranscriptRepository
) -> SessionManager:
    """The session registry/lifecycle, capped by MAX_CONCURRENT_SESSIONS.

    Built once at app startup and stored on ``app.state`` (see the app factory's
    lifespan). The pipeline factory is injected so tests can supply fakes; the shared
    transcript store (S-0) is threaded in so every pipeline persists through it.
    """
    return SessionManager(
        build_session_pipeline_factory(settings, transcript_repository),
        settings.max_concurrent_sessions,
    )


def get_session_manager(request: Request) -> SessionManager:
    manager: SessionManager | None = getattr(
        request.app.state, "session_manager", None
    )
    if manager is None:
        raise HTTPException(
            status_code=status.HTTP_503_SERVICE_UNAVAILABLE,
            detail="Session manager is not running.",
        )
    return manager


SessionManagerDep = Annotated[SessionManager, Depends(get_session_manager)]


async def require_internal_secret(
    settings: SettingsDep,
    x_internal_secret: Annotated[str | None, Header(alias="X-Internal-Secret")] = None,
) -> None:
    """Guard for /api/internal/* routes — the .NET side presents INTERNAL_API_SECRET.

    Fails closed if the server has no secret configured.
    """
    expected = settings.internal_api_secret
    if not expected or x_internal_secret != expected:
        raise HTTPException(
            status_code=status.HTTP_401_UNAUTHORIZED,
            detail="Invalid or missing internal API secret.",
        )
