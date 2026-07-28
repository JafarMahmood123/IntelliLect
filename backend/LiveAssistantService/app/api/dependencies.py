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
from app.infrastructure.brain.gemini_brain_client import GeminiBrainClient
from app.infrastructure.brain.ollama_brain_client import OllamaBrainClient
from app.infrastructure.config.settings import Settings, get_settings
from app.infrastructure.embeddings.constant_embedding_provider import (
    ConstantEmbeddingProvider,
)
from app.infrastructure.embeddings.ollama_embedding_provider import (
    OllamaEmbeddingProvider,
)
from app.infrastructure.feedback.livekit_feedback_sink import LiveKitFeedbackSink
from app.infrastructure.feedback.recording_feedback_sink import (
    FeedbackRecorder,
    RecordingFeedbackSink,
)
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
    """The BrainClient selected by BRAIN_PROVIDER: 'gemini' (hosted API) or 'ollama' (local).

    Both implement the same port, so the rest of the pipeline is provider-agnostic; switching is a
    config change (BRAIN_PROVIDER + the provider's model/key/url settings).
    """
    provider = settings.brain_provider.strip().lower()
    if provider == "gemini":
        return GeminiBrainClient(settings)
    if provider == "ollama":
        return OllamaBrainClient(settings)
    logger.warning("Unknown BRAIN_PROVIDER '%s'; falling back to ollama.", settings.brain_provider)
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


# --- Feedback recording (integration testing: AGENT_AUDIO_SOURCE=fake) ---------
def build_feedback_recorder() -> FeedbackRecorder:
    """Process-wide store of delivered feedback, shared by fake-mode pipelines and the
    feedback read endpoint. Harmless in production (nothing records into it)."""
    return FeedbackRecorder()


def get_feedback_recorder(request: Request) -> FeedbackRecorder:
    recorder: FeedbackRecorder | None = getattr(
        request.app.state, "feedback_recorder", None
    )
    if recorder is None:
        raise HTTPException(
            status_code=status.HTTP_503_SERVICE_UNAVAILABLE,
            detail="Feedback recorder is not running.",
        )
    return recorder


FeedbackRecorderDep = Annotated[FeedbackRecorder, Depends(get_feedback_recorder)]


# --- Session lifecycle (LA-6) -------------------------------------------------
def build_session_pipeline_factory(
    settings: Settings,
    transcript_repository: TranscriptRepository,
    feedback_recorder: FeedbackRecorder,
) -> SessionPipelineFactory:
    """Factory that assembles the full per-session loop from the phase components.

    In production (``AGENT_AUDIO_SOURCE == "livekit"``) each session gets its OWN
    LiveKit agent, used BOTH as the capture ``AudioSource`` and — because it implements
    ``AgentDataChannel`` — as the feedback channel, so feedback flows back over the same
    single connection. Stateless HTTP clients (embedder / retrieval / brain) are
    constructed per session too; they hold no per-session state.

    For integration testing (``AGENT_AUDIO_SOURCE == "fake"``) the audio ingress is a
    local WAV (``FakeAudioSource``) and feedback is recorded in-process
    (``RecordingFeedbackSink``) instead of published over LiveKit — so the FULL feedback
    intelligence (STT, boundary, retrieval, brain, pacing) still runs for real, without
    needing WebRTC media. Everything else is identical.

    One shared ``FeedbackPacer`` (LA-7) serves all sessions, keyed by session_id — the
    pipeline resets its session's pacing state on teardown. The shared
    ``transcript_repository`` (S-0) is durable/process-wide; each session gets its own
    ``TranscriptRecorder`` (per-session writer state) over it.
    """
    pacer = build_feedback_pacer(settings)
    fake_audio = settings.agent_audio_source.lower() == "fake"
    if fake_audio:
        logger.warning(
            "AGENT_AUDIO_SOURCE=fake — playing %s through the real pipeline; feedback "
            "is recorded in-process, not sent over LiveKit.",
            settings.fake_audio_wav_path or "<synth tone>",
        )

    def factory(session: SessionContext) -> SessionPipeline:
        if fake_audio:
            # Audio comes from a WAV; feedback is recorded (no LiveKit connection).
            audio: AudioSource = build_fake_audio_source(
                settings, settings.fake_audio_wav_path or None
            )
            sink: FeedbackSink = RecordingFeedbackSink(
                feedback_recorder, message_version=settings.feedback_message_version
            )
        else:
            agent = LiveKitAudioSource(settings)  # AudioSource + AgentDataChannel
            audio = agent
            sink = build_feedback_sink(settings, agent)
        stt = FasterWhisperSpeechToText(settings)
        # Drift needs the embedding model in RAM; on a constrained host that evicts the chat
        # model and makes replies slow. BOUNDARY_USE_EMBEDDER=false swaps in a no-op embedder
        # (pause/length boundaries only) so only the chat model stays resident.
        embedder: EmbeddingProvider = (
            OllamaEmbeddingProvider(settings)
            if settings.boundary_use_embedder
            else ConstantEmbeddingProvider()
        )
        boundary = build_boundary_detector(settings, embedder)
        brain = build_brain_client(settings)
        evaluator = build_idea_evaluator(
            settings, KnowledgeRetrievalClient(settings), brain
        )
        dispatcher = build_feedback_dispatcher(sink)
        recorder = TranscriptRecorder(
            transcript_repository,
            session.session_id,
            session.classroom_id,
            batch=settings.transcript_persist_batch,
        )
        # SMOKE TEST (temporary): when enabled, hand the pipeline the brain so it can send raw
        # transcript ideas straight to the LLM and deliver the reply. None => real pipeline.
        smoke_brain = brain if settings.assistant_smoke_test else None
        return SessionPipeline(
            session, audio, stt, boundary, evaluator, pacer, dispatcher, recorder,
            smoke_brain=smoke_brain,
        )

    return factory


def build_session_manager(
    settings: Settings,
    transcript_repository: TranscriptRepository,
    feedback_recorder: FeedbackRecorder,
) -> SessionManager:
    """The session registry/lifecycle, capped by MAX_CONCURRENT_SESSIONS.

    Built once at app startup and stored on ``app.state`` (see the app factory's
    lifespan). The pipeline factory is injected so tests can supply fakes; the shared
    transcript store (S-0) and feedback recorder are threaded in so every pipeline
    persists / records through them.
    """
    return SessionManager(
        build_session_pipeline_factory(
            settings, transcript_repository, feedback_recorder
        ),
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
