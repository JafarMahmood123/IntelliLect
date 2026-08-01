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
    # Which STT engine transcribes: "groq" (hosted Whisper, ~4.5x realtime) or "faster_whisper"
    # (local CPU). Local cannot keep up with live speech on a laptop — measured on one 11s clip:
    # small.en beam=5 = 0.2x realtime, tiny.en beam=1 = 1.8x but with the worst accuracy. Groq is
    # both faster AND more accurate, so it is the default for live sessions; local remains the
    # offline/no-network option. There is deliberately NO automatic fallback between them: a
    # 20x-slower silent downgrade is harder to diagnose than a loud failure.
    stt_provider: str = "faster_whisper"

    # --- Groq STT (hosted); used when stt_provider == "groq" ---
    # The key is a SECRET: keep it in LiveAssistantService/.env (GROQ_API_KEY), never in compose.
    # NOTE: Groq blocks known VPN/datacenter exit IPs with HTTP 403. If a VPN is in use, pin it to
    # a server that is not blocked, or exclude api.groq.com from the tunnel.
    groq_api_key: str = ""
    groq_base_url: str = "https://api.groq.com/openai/v1"
    # whisper-large-v3-turbo | whisper-large-v3. Both measured ~2.4s for an 11s window, because
    # latency here is dominated by UPLOAD rather than compute — so large-v3 costs nothing extra in
    # practice, while turbo is the safer pick on a slow link.
    groq_stt_model: str = "whisper-large-v3-turbo"
    groq_timeout_seconds: float = 60.0

    # --- Local engine (faster_whisper) settings; ignored when stt_provider == "groq" ---
    stt_model: str = "base.en"  # faster-whisper English model id (tiny.en/base.en/small.en/...)
    stt_device: str = "cpu"  # cpu | cuda
    stt_compute_type: str = "int8"  # int8 (low RAM on cpu) | float16 | ...
    stt_language: str = "en"  # English only for now (Arabic deferred)
    stt_chunk_seconds: float = 3.0  # audio accumulated before a transcription step
    # Trailing silence that marks a segment boundary. 1.5, NOT 0.8: at 0.8 a real session had
    # sentences cut mid-phrase ("Also, we are going to" / "talk about the new points that"), and
    # each fragment is then transcribed with no surrounding context — which is how
    # "/api/notifications/device-tokens" came back as "Vault, Registur, Device". Fragmenting the
    # audio destroys the technical vocabulary the grounded evaluation depends on.
    # Must stay well BELOW boundary_pause_seconds, or a breath would end an idea.
    stt_pause_seconds: float = 1.5
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
    # Decoder seed for domain vocabulary. KEEP THIS EMPTY unless you re-measure afterwards.
    # A bare word list ("Gemini, LiveKit, IntelliLect") made Whisper REGURGITATE the prompt on
    # low-signal windows: 11 of 32 segments in a real session (34%) came back as pure prompt echo
    # — 'Intelli' x8, 'Intelli, Intelli, Intelli', 'Intelli, LiveKit'. Those are fabricated words
    # that entered the idea text the brain evaluates, and each cost a ~2s embedding round trip.
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

    # --- Gemini STT; used when stt_provider == "gemini" ---
    # LAST-RESORT escape hatch for when Groq is hard-blocked (Groq 403s every request from a
    # datacenter/VPN exit IP, while Gemini serves the same container fine). Reuses gemini_api_key /
    # gemini_base_url.
    #
    # ⚠ MEASURED TOO SLOW FOR LIVE USE: 0.5-0.67x realtime on a 10s window (median 17.4s) vs Groq's
    # ~5x. Below 1.0x means it cannot keep up with a live lecture and falls further behind as the
    # class runs. It also hallucinated words on 3 of 4 pure-silence windows. Use it only when the
    # network path genuinely cannot be fixed; fixing the route is the real answer to a 403.
    # Leave stt_language empty here to transcribe whatever language is spoken (Whisper is pinned).
    #
    # Needs a model with AUDIO input support; the *-latest aliases are used for the same
    # quota reason as gemini_model (pinned 2.0/2.5 versions are 429/404 on this project's key).
    gemini_stt_model: str = "gemini-flash-latest"
    # Audio windows are much larger than text prompts, so this is separate from (and longer than)
    # embedding_timeout_seconds.
    gemini_stt_timeout_seconds: float = 90.0
    # Transcription has nothing worth reasoning about, so keep thinking at the floor. Blank omits
    # thinkingConfig entirely (correct for non-3.x models, which reject the field).
    gemini_stt_thinking_level: str = "low"

    # --- Idea boundary detection (LA-3) ---
    # Segments the transcript into "ideas". Semantic drift is measured by embedding
    # finalized segments (via the EmbeddingProvider) — the ONLY model use here, and
    # even that is faked in tests. The caps are safety nets so a long monologue always
    # yields boundaries.
    # Cosine distance at which the next segment counts as a NEW idea rather than a continuation.
    # This value is PAIRED WITH THE EMBEDDER: different models/task types put topic boundaries at
    # different absolute distances, so it must be re-measured whenever the embedder changes (see
    # gemini_embedding_task_type for the measurements behind the default below).
    # Calibrated for gemini-embedding-001 + SEMANTIC_SIMILARITY, where same-topic pairs measured
    # <=0.159 and new-topic pairs >=0.305. 0.25 sits between them with margin on both sides, and
    # leans slightly toward MERGING — wrongly splitting one explanation in half is worse than
    # occasionally carrying a sentence of the next topic along.
    boundary_drift_threshold: float = 0.25
    # Silence that ends an idea outright, regardless of topic.
    #
    # This is silence measured BETWEEN two finalized segments — i.e. silence ON TOP OF the
    # stt_pause_seconds the STT already consumed before it finalized the previous segment. The
    # real wall-clock gap is therefore roughly stt_pause_seconds + this. At 1.5 + 2.0 that is a
    # ~3.5s hush: a deliberate "and that concludes that point", not an inter-sentence breath.
    #
    # Keep it WELL ABOVE stt_pause_seconds. Every segment arrives flagged followed_by_pause (the
    # STT finalizes on silence), so if this were small the pause rule would end an idea after
    # every sentence and semantic drift would never get to decide anything — which is exactly the
    # behaviour this setting was raised to fix.
    boundary_pause_seconds: float = 2.0
    boundary_max_seconds: float = 90.0  # hard cap on idea duration
    boundary_max_tokens: int = 400  # hard cap on idea length (whitespace tokens)
    boundary_min_tokens: int = 20  # ignore/merge ideas smaller than this
    # When false, the boundary detector uses a no-op (zero-vector) embedder instead of the
    # Ollama one, so drift is disabled and idea boundaries come from PAUSE + length caps only.
    # This keeps the embedding model OUT of Ollama's RAM on constrained hosts, so the chat model
    # stays resident (no model-swap reload) and replies are fast. Default true (full drift).
    boundary_use_embedder: bool = True
    # How many segment embeddings may be in flight at once. The embeddings are independent of each
    # other, but the boundary DECISIONS are not, so results are still applied in source order —
    # only the waiting overlaps. Measured motivation: 90.6s of embedding inside a 143s lecture, a
    # 2.01s median against a 3.05s median gap between segments (61% occupancy), with a 1.39s
    # minimum gap and one 10.5s outlier that stalled everything behind it.
    # Bounded, not unlimited: the embedder is a rate-limited hosted API, so an unbounded fan-out
    # would turn a burst of speech into a burst of 429s. 1 restores the old serial behaviour.
    boundary_embed_lookahead: int = 4

    # --- Embeddings (LA-3 drift; local Ollama, HTTP-only — no model weights here) ---
    # Used to embed transcript segments for drift measurement. On Linux,
    # host.docker.internal resolves to the host via the compose extra_hosts mapping;
    # host Ollama must listen on 0.0.0.0:11434 with the embedding model pulled.
    ollama_base_url: str = "http://host.docker.internal:11434"
    ollama_auth_token: str = ""  # optional bearer token, sent only if set
    embedding_model: str = "qwen3-embedding"
    embedding_timeout_seconds: float = 60.0

    # Which EmbeddingProvider measures drift: "ollama" (local, a second model in RAM) or "gemini"
    # (hosted, no local RAM, ~0.85s per segment on the boundary hot path). Mirrors brain_provider.
    embedding_provider: str = "ollama"

    # --- Gemini embeddings; used when embedding_provider == "gemini" ---
    # Reuses gemini_api_key / gemini_base_url / embedding_timeout_seconds.
    gemini_embedding_model: str = "gemini-embedding-001"
    # Matryoshka truncation: 768 halves latency vs the native 3072 with no measured loss of
    # topic separation. 0 = native. NOTE the provider MUST re-normalize after truncation —
    # Gemini returns a 768-dim vector with norm ~0.59, and cosine drift assumes unit vectors.
    gemini_embedding_dimensions: int = 768
    # SEMANTIC_SIMILARITY tunes the space for "are these about the same thing" — precisely the
    # drift question. It separates topics better BUT compresses absolute distances, so it needs a
    # much lower boundary_drift_threshold. Measured on real sentence pairs:
    #
    #   taskType              same-topic (max)   new-topic (min)   good threshold
    #   (none)                     0.335              0.507             ~0.42
    #   SEMANTIC_SIMILARITY        0.159              0.305             ~0.23
    #
    # Changing this WITHOUT re-tuning boundary_drift_threshold silently breaks drift: at the old
    # 0.35 default, SEMANTIC_SIMILARITY would never split anything.
    gemini_embedding_task_type: str = "SEMANTIC_SIMILARITY"

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

    # --- Quiz generation: MCQs from the idea the teacher just finished ---
    # Separate knobs from evaluation because the shape of the work is different: a suggestion is
    # one short paragraph, a quiz is several questions with several options each, so it needs a
    # far larger generation cap and longer to produce. Note the thinking-token warning on
    # gemini_model — thinking is charged against this cap too, and a truncated reply is a lost quiz.
    quiz_max_tokens: int = 2048
    quiz_timeout_seconds: float = 90.0
    # Slightly above eval_temperature: identical phrasing every time makes for dull distractors,
    # while too much invention costs accuracy. The response schema constrains the shape regardless.
    quiz_temperature: float = 0.4
    # Below this many estimated tokens, the newest idea alone is too thin to build questions from
    # and the generator reaches back through the retained ideas for more context. A boundary that
    # fired on a pause can be only a few seconds of speech.
    quiz_min_idea_tokens: int = 60
    # How many finished ideas to retain per session for the generator to draw on.
    quiz_idea_history: int = 3
    # --- Whole-lesson quizzes ---
    # A quick test is built from the ideas just finished; a full quiz is built from the session's
    # whole TRANSCRIPT, which is durable and covers material the bounded idea history has long
    # since evicted.
    #
    # The character cap is a guard against a two-hour lecture blowing the model's context. When it
    # bites, the TAIL is kept — see QuizGenerator for why the end is the better half to keep.
    quiz_full_max_chars: int = 24_000
    # Wider than retrieval_top_k because a whole lesson spans more ground than one idea, and a
    # single idea's worth of material would leave most of the quiz ungrounded.
    quiz_full_top_k: int = 12

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
