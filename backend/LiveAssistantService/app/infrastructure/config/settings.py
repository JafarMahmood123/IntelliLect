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

    # --- Observability ---
    log_level: str = "INFO"  # root log level (DEBUG/INFO/WARNING/...)

    # --- Placeholders for later phases (unused this phase) ---
    # KnowledgeService base URL for classroom-scoped RAG retrieval (RetrievalClient).
    knowledge_base_url: str = ""
    # Shared secret for internal service-to-service calls (X-Internal-Secret).
    internal_api_secret: str = ""

    @property
    def livekit_configured(self) -> bool:
        """True only when every credential needed to join a real room is present."""
        return bool(self.livekit_url and self.livekit_api_key and self.livekit_api_secret)


@lru_cache
def get_settings() -> Settings:
    return Settings()  # type: ignore[call-arg]  # values come from the environment
