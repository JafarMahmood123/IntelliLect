from functools import lru_cache

from pydantic_settings import BaseSettings, SettingsConfigDict


class Settings(BaseSettings):
    """Application configuration, sourced from environment variables (.env in dev).

    Read once and cached via ``get_settings()``. Case-insensitive; unknown env vars
    are ignored so this service can share a compose ``.env`` with its siblings.
    """

    model_config = SettingsConfigDict(
        env_file=".env",
        env_file_encoding="utf-8",
        case_sensitive=False,
        extra="ignore",
    )

    # --- LiveKit (server-side agent) ---
    # URL of the LiveKit server (e.g. wss://<host> or ws://livekit:7880). The API
    # key/secret mint the agent's join token. All three are required for real
    # capture, but the service still starts and /health is ok without them (see
    # livekit_configured) — offline development uses FakeAudioSource instead.
    livekit_url: str = ""
    livekit_api_key: str = ""
    livekit_api_secret: str = ""
    # Identity the agent joins the room under (kept distinct from any teacher/student).
    agent_identity: str = "ai-assistant"

    # --- Audio normalization target (what downstream STT consumes) ---
    # Incoming LiveKit audio is resampled/downmixed to this so later stages are
    # decoupled from LiveKit's native capture format. STT (faster-whisper) assumes
    # 16kHz mono, so target_sample_rate should stay 16000.
    target_sample_rate: int = 16000
    target_channels: int = 1  # mono

    # --- Speech-to-text (LA-2): streaming English STT via faster-whisper/CTranslate2 ---
    # STT runs continuously during a session ALONGSIDE the embedder and the 7B brain,
    # so keep the model small (English-only + int8) to fit the shared RAM budget;
    # larger models are a later, hardware-dependent upgrade. Uses its OWN model — no
    # Ollama, no torch/transformers (CTranslate2 is a separate inference engine).
    stt_model: str = "base.en"  # faster-whisper English model id (tiny.en/base.en/small.en/...)
    stt_device: str = "cpu"  # cpu | cuda
    stt_compute_type: str = "int8"  # int8 (low RAM on cpu) | float16 | ...
    stt_language: str = "en"  # English only for now (Arabic deferred)
    stt_chunk_seconds: float = 3.0  # audio accumulated before a transcription step
    stt_pause_seconds: float = 0.8  # trailing silence that marks a segment boundary

    # --- Idea boundary detection (LA-3) ---
    # Segments the transcript into "ideas". Semantic drift is measured by embedding
    # finalized segments (via the EmbeddingProvider) — the ONLY model use here, and
    # even that is faked in tests. The caps are safety nets so a long monologue always
    # yields boundaries.
    boundary_drift_threshold: float = 0.35  # cosine distance marking a new idea
    # Pause length that implies a thought break. Reuses the LA-2 pause concept
    # (stt_pause_seconds): the followed_by_pause flag already encodes it, and this
    # also gates silent-gap-between-segments pauses.
    boundary_pause_seconds: float = 0.8
    boundary_max_seconds: float = 90.0  # hard cap on idea duration
    boundary_max_tokens: int = 400  # hard cap on idea length (whitespace tokens)
    boundary_min_tokens: int = 20  # ignore/merge ideas smaller than this

    # --- Embeddings (LA-3 drift; local Ollama, HTTP-only — no model weights here) ---
    # Used to embed transcript segments for drift measurement. On Linux,
    # host.docker.internal resolves to the host via the compose extra_hosts mapping;
    # host Ollama must listen on 0.0.0.0:11434 with the embedding model pulled.
    ollama_base_url: str = "http://host.docker.internal:11434"
    ollama_auth_token: str = ""  # optional bearer token, sent only if set
    embedding_model: str = "qwen3-embedding"
    embedding_timeout_seconds: float = 60.0

    # --- Retrieval (LA-4): classroom material via the existing KnowledgeService ---
    # Retrieval goes over HTTP to KnowledgeService (it owns the vector DB); the idea
    # TEXT is sent as the query and KnowledgeService embeds + searches internally.
    knowledge_base_url: str = ""  # e.g. http://knowledge-service:8080
    internal_api_secret: str = ""  # shared secret, sent as X-Internal-Secret
    retrieval_top_k: int = 6  # chunks requested per idea
    retrieval_min_score: float = 0.25  # below this = "no relevant material" (short-circuit)

    # --- Evaluation / brain (LA-4): local generative model in host Ollama ---
    # Reuses OLLAMA_BASE_URL/OLLAMA_AUTH_TOKEN (above). This service has its own
    # live-assistant-specific evaluation prompt; the model matches KnowledgeService's
    # generation model. No weights in the container — every call is HTTP to Ollama.
    eval_model: str = "qwen2.5:7b-instruct"
    eval_temperature: float = 0.2
    eval_timeout_seconds: float = 60.0
    eval_max_tokens: int = 512  # num_predict

    # --- Feedback delivery (LA-5): private, teacher-only ---
    # Primary transport is a reliable LiveKit data message from the agent's existing
    # room connection, targeted to the teacher identity ONLY. "signalr" (StreamHub) is
    # a documented future alternative. message_version stamps the wire contract.
    feedback_transport: str = "livekit"  # livekit | signalr (future)
    feedback_message_version: int = 1

    # --- Observability ---
    log_level: str = "INFO"  # root log level (DEBUG/INFO/WARNING/...)

    @property
    def livekit_configured(self) -> bool:
        """True only when every credential needed to join a real room is present."""
        return bool(self.livekit_url and self.livekit_api_key and self.livekit_api_secret)


@lru_cache
def get_settings() -> Settings:
    return Settings()  # type: ignore[call-arg]  # values come from the environment
