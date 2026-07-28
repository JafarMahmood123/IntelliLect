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

    # --- Audio ingress selection (integration testing) ---
    # "livekit" (default, production): capture the teacher's audio from the LiveKit
    # room. "fake": play a local WAV through the REAL rest of the pipeline (STT ->
    # boundary -> retrieval -> brain -> pacing) instead of joining LiveKit. This exists
    # so the full feedback loop can be exercised end-to-end in environments where WebRTC
    # media cannot flow (e.g. CI, Docker Desktop + VPN). When "fake", feedback is
    # recorded in-process (readable via GET /api/internal/sessions/{id}/feedback) rather
    # than published over a LiveKit data channel. Production behavior is unchanged unless
    # AGENT_AUDIO_SOURCE is explicitly set to "fake".
    agent_audio_source: str = "livekit"
    fake_audio_wav_path: str = ""  # WAV played when agent_audio_source == "fake"

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
    # CPU cores faster-whisper/CTranslate2 may use. On an 8-core host, STT and the local
    # LLM otherwise BOTH grab every core and oversubscribe: while the teacher keeps talking,
    # STT transcribes new windows on all cores at the same instant Ollama wants all cores to
    # generate the reply -> context-thrash, and the SAME short reply takes 5s once and 12s the
    # next. Capping STT leaves cores free for generation (see eval_num_thread). base.en int8
    # stays comfortably faster-than-realtime at 2 threads. 0 = CTranslate2 default (all cores).
    stt_cpu_threads: int = 2
    # Whisper invents repeated tokens ("okay okay okay", ". . .") when handed near-silent or noisy
    # audio. faster-whisper's built-in Silero VAD drops non-speech regions BEFORE transcription,
    # which removes almost all of that hallucination for a little extra CPU. Disable only to inspect
    # the raw model output. stt_vad_min_silence_ms is how much silence closes a VAD speech chunk.
    stt_vad_filter: bool = True
    stt_vad_min_silence_ms: int = 500
    # Silero VAD trims tightly to detected speech, which clips the first/last phoneme of an
    # utterance — the model then guesses at the stub ("I love you Manai" for "...man, I"). Padding
    # each speech chunk gives it the leading/trailing context to resolve word edges correctly.
    stt_vad_speech_pad_ms: int = 200

    # Decoding width. beam_size=1 is greedy: fastest, and the first plausible token wins even when
    # a later one would have scored better over the whole utterance — the usual source of confident
    # nonsense on proper nouns ("Gemini" -> "Jimmy now"). 5 is faster-whisper's default and costs
    # roughly 2x decode time, which is affordable now that the local LLM no longer competes for
    # cores (see stt_cpu_threads). Drop to 1 only if CPU becomes the bottleneck again.
    stt_beam_size: int = 5

    # Seeds the decoder's context with domain vocabulary so names and jargon that Whisper has no
    # reason to expect are still spelled correctly. Keep it SHORT — it is prepended to every
    # window, so it costs tokens on each call and a long prompt can bias the model into echoing it.
    # e.g. "Gemini, LiveKit, IntelliLect, Kubernetes"
    stt_initial_prompt: str = ""

    # Emit INTERIM (unstable, mid-utterance) segments every stt_chunk_seconds.
    # Default FALSE: nothing downstream consumes them — BoundaryDetector ignores non-final
    # segments and the transcript recorder refuses them — yet each interim re-transcribes the
    # WHOLE utterance-so-far, so leaving this on roughly doubles STT CPU for output that is
    # discarded, and that wasted work delays the FINAL transcription that does matter.
    # Turn on only to debug the streaming state machine or to drive a live-caption UI.
    stt_emit_interim: bool = False

    # Hard cap on the re-transcribed window. Without it the buffer only clears on a pause, so a
    # teacher talking through their pauses grows it without bound and every re-transcription gets
    # slower (the work is superlinear in utterance length). At the cap the segment is finalized and
    # a new one starts. 30s matches Whisper's own native window, beyond which it internally chunks
    # anyway, so this costs no accuracy.
    stt_max_window_seconds: float = 30.0

    # Log each transcribed window's TEXT at INFO. Development only: it is the one place the service
    # writes transcript content to logs, which violates the privacy rule the rest of the pipeline
    # follows (counts/ids/types only). Default FALSE so production never leaks lecture content.
    stt_debug_log_text: bool = False

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
    # When false, the boundary detector uses a no-op (zero-vector) embedder instead of the
    # Ollama one, so drift is disabled and idea boundaries come from PAUSE + length caps only.
    # This keeps the embedding model OUT of Ollama's RAM on constrained hosts, so the chat model
    # stays resident (no model-swap reload) and replies are fast. Default true (full drift).
    boundary_use_embedder: bool = True

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
    eval_max_tokens: int = 512  # num_predict / maxOutputTokens (provider-agnostic generation cap)

    # --- Brain provider selection ---
    # Which BrainClient backs the assistant: "ollama" (local, CPU) or "gemini" (Google AI Studio,
    # hosted). Switch providers with ONE env var — no code change. eval_temperature/eval_max_tokens/
    # eval_timeout_seconds apply to whichever provider is selected.
    brain_provider: str = "ollama"

    # --- Gemini brain (Google AI Studio); used when brain_provider == "gemini" ---
    # The key is a SECRET: keep it in .env (GEMINI_API_KEY), never in the compose file or git.
    # model/base_url are configurable so a new model or API version is a config change.
    # gemini_generation_config_json is a JSON object merged into the request's generationConfig,
    # so extra request fields (topP, topK, stopSequences, …) attach without touching code.
    gemini_api_key: str = ""
    # A *-latest alias rather than a pinned version: the pinned 2.0/2.5 models are already either
    # quota-zero on the free tier or closed to new users, so a hard-coded version silently rots
    # into 429/404. Note the 3.x models THINK, and thinking tokens are charged against
    # eval_max_tokens — keep that cap generous (>=1024) or the reply truncates before it finishes.
    gemini_model: str = "gemini-flash-lite-latest"
    gemini_base_url: str = "https://generativelanguage.googleapis.com/v1beta"
    gemini_generation_config_json: str = ""
    # Cores Ollama may use to generate a reply (options.num_thread). Paired with
    # stt_cpu_threads so STT + generation don't oversubscribe the 8-core host: 2 for STT
    # + 6 here = 8, saturated but not thrashing. This is the main lever that pulls the
    # smoke reply from ~5-12s down toward ~3-5s. 0 = Ollama's own default (all cores).
    eval_num_thread: int = 6

    # --- Feedback delivery (LA-5): private, teacher-only ---
    # Primary transport is a reliable LiveKit data message from the agent's existing
    # room connection, targeted to the teacher identity ONLY. "signalr" (StreamHub) is
    # a documented future alternative. message_version stamps the wire contract.
    feedback_transport: str = "livekit"  # livekit | signalr (future)
    feedback_message_version: int = 1

    # --- Session lifecycle (LA-6) ---
    # One agent pipeline per active session; start beyond this cap is rejected (503).
    max_concurrent_sessions: int = 20

    # --- Transcript persistence (S-0): durable teacher transcript per session ---
    # FINAL transcript segments are persisted incrementally so a mid-session crash
    # doesn't lose the transcript, and the ordered transcript can be assembled for the
    # (later) session-summary feature. When TRANSCRIPT_DB_URL is empty the service runs
    # fully offline against an in-memory store (non-durable) — mirroring how LiveKit /
    # Ollama / KnowledgeService are optional here. Set it (Postgres, asyncpg driver) to
    # persist for real; the Alembic migration provisions the schema.
    transcript_db_url: str = ""  # e.g. postgresql+asyncpg://user:pass@host:5432/db
    # Flush cadence for the background writer: persist after every N final segments.
    # Default 1 = persist each final segment immediately (most crash-resilient); a
    # larger value trades a little durability for fewer writes.
    transcript_persist_batch: int = 1

    # --- Pacing, safety & suppression (LA-7) ---
    # Gates delivery so the assistant never floods the teacher. Pure decision logic.
    feedback_min_interval_sec: float = 45.0  # min seconds between delivered suggestions
    feedback_confidence_min: float = 0.5  # drop suggestions below this confidence
    feedback_default_confidence: float = 0.6  # used when the model omits confidence
    feedback_dedup_window_sec: float = 300.0  # look-back window for duplicate suppression
    feedback_dedup_similarity: float = 0.85  # token-similarity threshold for a duplicate
    feedback_max_per_session: int = 0  # hard cap on delivered suggestions (0 = no cap)

    # --- Observability (LA-8) ---
    log_level: str = "INFO"  # root log level (DEBUG/INFO/WARNING/...)
    metrics_enabled: bool = True  # expose /metrics and record Prometheus metrics

    # --- SMOKE TEST (temporary) ---
    # When true, the live pipeline BYPASSES retrieval + grounded evaluation + pacing and instead
    # sends each completed idea's raw transcript to the chat model, delivering the model's raw
    # reply straight to the teacher. Proves the transcript -> LLM -> teacher path while the real
    # assistant is tuned. Set ASSISTANT_SMOKE_TEST=false to restore the real assistant.
    assistant_smoke_test: bool = False

    @property
    def livekit_configured(self) -> bool:
        """True only when every credential needed to join a real room is present."""
        return bool(self.livekit_url and self.livekit_api_key and self.livekit_api_secret)


@lru_cache
def get_settings() -> Settings:
    return Settings()  # type: ignore[call-arg]  # values come from the environment
