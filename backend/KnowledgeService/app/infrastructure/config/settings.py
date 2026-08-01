from functools import lru_cache

from pydantic_settings import BaseSettings, SettingsConfigDict


class Settings(BaseSettings):
    """Application configuration, sourced from environment variables (.env in dev).

    Read once and cached via get_settings(). Alembic env.py also imports this so
    the migration's vector dimension stays in sync with the running app.
    """

    model_config = SettingsConfigDict(
        env_file=".env",
        env_file_encoding="utf-8",
        case_sensitive=False,
        extra="ignore",
    )

    # --- Database ---
    database_url: str  # e.g. postgresql+asyncpg://user:pass@host:5432/db

    # --- Embeddings ---------------------------------------------------------------------
    # Which provider embeds documents and queries: "gemini" (hosted, no local RAM, multilingual)
    # or "ollama" (local host model). Mirrors LiveAssistantService's EMBEDDING_PROVIDER.
    #
    # ⚠ THIS IS NOT A FREE SWITCH. embedding_dim below sets the pgvector COLUMN WIDTH (see
    # models.py and alembic/versions/, both of which import it from here), so changing provider or
    # model means: (1) an Alembic migration for the column + HNSW index, and (2) re-embedding every
    # stored chunk. Vectors from two different models live in different spaces — mixing them
    # returns confident nonsense rather than an error, so a partial migration is worse than none.
    #
    # DEFAULTS "gemini" TO STAY CONSISTENT WITH embedding_dim BELOW. This used to default to
    # "ollama" while embedding_dim defaulted to 3072 — but qwen3-embedding returns 1024, so the
    # two defaults contradicted each other and a run without compose's overrides would fail. The
    # provider and the width have to agree, and gemini/3072 is what actually deploys.
    embedding_provider: str = "gemini"

    # On Linux, host.docker.internal resolves to the host via the compose
    # `extra_hosts: host-gateway` mapping; host Ollama must listen on 0.0.0.0:11434.
    ollama_base_url: str = "http://host.docker.internal:11434"
    # Optional bearer token. Sent as `Authorization: Bearer <token>` ONLY if set.
    ollama_auth_token: str = ""
    embedding_model: str = "qwen3-embedding"  # used when embedding_provider == "ollama"
    embedding_timeout_seconds: float = 60.0

    # --- Gemini embeddings; used when embedding_provider == "gemini" ---
    # The key is a SECRET: keep it in .env (GEMINI_API_KEY), never in compose.
    gemini_api_key: str = ""
    gemini_base_url: str = "https://generativelanguage.googleapis.com/v1beta"
    gemini_embedding_model: str = "gemini-embedding-001"

    # Vector width, and therefore the pgvector column type. MUST match whatever the configured
    # provider returns — the provider raises if it does not, because a mismatch otherwise fails
    # at INSERT after a long ingestion run.
    #   qwen3-embedding       -> 1024
    #   gemini-embedding-001  -> 3072 native, or 768 / 1536 via Matryoshka truncation
    # NOTE ON 3072: pgvector's HNSW index refuses more than 2000 dimensions for the `vector` type,
    # so the column is `halfvec` (indexable to 4000). fp16 costs nothing meaningful for cosine
    # ranking and halves storage. Below 2001 either type works.
    embedding_dim: int = 3072

    # OLLAMA ONLY. Prepended to QUERIES (documents embedded raw) to fake asymmetric retrieval;
    # the "Instruct:" form is a qwen convention. The Gemini provider ignores this and uses proper
    # taskType=RETRIEVAL_QUERY / RETRIEVAL_DOCUMENT instead, which is the real mechanism.
    retrieval_instruction: str = (
        "Instruct: Given a search query, retrieve relevant passages that answer it\n"
        "Query: {query}"
    )

    # --- OCR (Phase 3): selective Tesseract cascade, English only ---
    # No model weights either — pytesseract shells out to the tesseract binary.
    ocr_lang: str = "eng"
    ocr_dpi: int = 300  # rasterization DPI for scanned PDF pages
    ocr_max_workers: int = 2  # bounded OCR pool — the RAM cap
    ocr_min_image_px: int = 200  # skip images whose max(width, height) is below this
    ocr_max_image_px: int = 2000  # downscale larger images to this long edge before OCR
    ocr_min_confidence: float = 45.0  # drop OCR output below this mean word confidence
    ocr_min_chars: int = 8  # drop trivially short OCR output

    # --- Chunking (Phase 4) ---
    # "structural" needs no model (default, fully offline). "semantic" uses the
    # EmbeddingProvider to place topic-shift breakpoints (requires a live model).
    chunking_strategy: str = "structural"
    chunk_max_tokens: int = 512  # hard cap on tokens per chunk
    chunk_overlap_tokens: int = 64  # overlap carried between consecutive chunks
    chunk_min_tokens: int = 64  # merge trailing fragments smaller than this
    semantic_breakpoint_percentile: int = 90  # distance percentile that marks a boundary

    # --- Reindex (re-embed stored chunks after an embedder change) ---
    # Chunks per batch. Each batch is one DB transaction and one fan-out of embedding calls, so
    # this trades round trips against how much work a crash mid-sweep discards. Bounded low
    # because embedContent is one HTTP call per chunk.
    reembed_batch_size: int = 32

    # --- Ingestion worker (Phase 5) ---
    ingest_max_concurrency: int = 1  # concurrent worker tasks — the RAM cap
    ingest_queue_max: int = 100  # bounded in-process job queue size
    embed_batch_size: int = 32  # chunk texts per embed_documents call

    # --- Super-admin knowledge-base management ---
    admin_list_default_page_size: int = 20
    admin_list_max_page_size: int = 100
    reindex_bulk_max: int = 50  # max files a single classroom bulk-reindex may enqueue (7ب)
    reindex_enqueue_retries: int = 3  # per-file retries when the queue is momentarily full (7د)
    reindex_enqueue_retry_seconds: float = 0.2

    # --- Ingestion lifecycle & robustness (Phase 8) ---
    ingest_max_attempts: int = 3  # after this many attempts, mark permanently Failed
    ingest_retry_base_seconds: float = 2.0  # exponential backoff base
    ingest_retry_max_seconds: float = 30.0  # backoff cap
    stale_processing_minutes: int = 15  # Processing older than this is considered stale
    stale_recovery_on_startup: bool = True  # re-queue stale Processing docs at startup

    # --- Search / retrieval (Phase 7) ---
    search_default_top_k: int = 8  # results returned when topK is not supplied
    search_max_top_k: int = 50  # upper clamp on a requested topK

    # --- Generation / answering (Phase 10) ---
    # Which backend produces completions: "gemini" (hosted) or "ollama" (local host model).
    # Mirrors embedding_provider, and applies to BOTH answering and summarization — they
    # share the GenerationProvider port, and each keeps its own model/temperature/budget.
    #
    # Unlike embedding_provider this IS a free switch: completions are text, so nothing is
    # persisted in a model-specific format and you can flip it back with no migration.
    generation_provider: str = "ollama"
    # Local generative model in host Ollama (reuses OLLAMA_BASE_URL / OLLAMA_AUTH_TOKEN).
    # Used only when generation_provider == "ollama".
    generation_model: str = "qwen2.5:7b-instruct"
    generation_timeout_seconds: float = 120.0
    generation_temperature: float = 0.2
    generation_max_tokens: int = 1024  # num_predict / maxOutputTokens
    answer_top_k: int = 6  # chunks retrieved for answer context
    context_max_tokens: int = 6000  # token budget for the packed context block

    # --- Gemini generation; used when generation_provider == "gemini" ---
    # Reuses gemini_api_key / gemini_base_url from the embedding block above.
    # A *-latest alias, not a pinned version: pinned 2.0/2.5 models go quota-zero or 404 on
    # the free tier over time, so a hard-coded version silently rots into an error.
    gemini_generation_model: str = "gemini-flash-latest"
    gemini_summary_model: str = "gemini-flash-latest"
    # Thinking tokens are charged against maxOutputTokens on 3.x models, so a summary can
    # spend its whole budget reasoning and return NOTHING with finishReason=MAX_TOKENS.
    # Keep this low; blank omits thinkingConfig entirely, which older models require.
    gemini_thinking_level: str = "low"

    # --- Session summary (S-1) ---
    # Turns a lecture transcript (fetched from LiveAssistantService) into a structured
    # Markdown summary. Runs on whichever backend generation_provider selects, but with its
    # own generation parameters so summarization can be tuned independently of answering.
    # This phase STOPS at Markdown (PDF is S-2).
    # NOTE: summary_model is the OLLAMA model name; the Gemini equivalent is
    # gemini_summary_model. Only the one matching generation_provider is read.
    summary_model: str = "qwen2.5:7b-instruct"  # reuse the generation model by default
    summary_temperature: float = 0.3
    summary_max_tokens: int = 1500  # num_predict for a summary pass
    summary_grounding_enabled: bool = True  # ground key terms in classroom material
    summary_grounding_top_k: int = 6  # chunks retrieved PER query window
    # Grounding queries are excerpts of the transcript. One excerpt only ever retrieves
    # material about whatever the lecture opened with, so anything taught later is
    # "grounded" against chunks that never mention it — the failure that let a late-lecture
    # error through unchallenged. Sample several excerpts spanning the whole transcript
    # instead, then merge. Cost is one extra embed + vector search per window.
    summary_grounding_query_windows: int = 4
    # Ceiling on the MERGED, de-duplicated supporting set, so windows * top_k cannot
    # quietly grow the prompt without bound on a long lecture.
    summary_grounding_max_chunks: int = 10
    # Cap on transcript tokens fed to the model in a single pass. Longer transcripts are
    # summarized map-reduce (chunk summaries -> synthesis) so a long lecture still fits.
    summary_transcript_max_tokens: int = 8000
    # LiveAssistantService internal transcript endpoint (S-0). Empty in offline dev/tests
    # (the FakeTranscriptClient is used instead). Reuses INTERNAL_API_SECRET below.
    live_assistant_base_url: str = ""  # e.g. http://live-assistant-service:8080

    # --- Session summary storage & trigger (S-3) ---
    # On session end, the summary pipeline uploads the Markdown + PDF to object storage
    # and publishes a SessionSummaryReadyMessage for ClassroomService (S-4). The
    # SUMMARY_S3_* values default to the generic S3_* above when blank, so the summaries
    # may live in the same (recordings) bucket. Offline tests use fakes and need none.
    summary_s3_bucket: str = ""  # falls back to S3_BUCKET_NAME when blank
    summary_s3_region: str = ""  # falls back to S3_REGION when blank
    summary_s3_access_key: str = ""  # falls back to S3_ACCESS_KEY when blank
    summary_s3_secret_key: str = ""  # falls back to S3_SECRET_KEY when blank
    summary_s3_endpoint: str = ""  # falls back to S3_SERVICE_URL when blank (optional)
    # Object-key template. Placeholders: {classroom_id}, {session_id}, {ext} (md/pdf).
    summary_s3_key_template: str = "summaries/{classroom_id}/{session_id}.{ext}"
    summary_trigger_enabled: bool = True  # feature flag for the session-end trigger

    # --- Summary retry / dedup / outbox ---
    # Summaries had NO retry: one attempt, and a transient 429 or a slow transcript meant Failed
    # forever. They now use the same machinery ingestion does (claim, attempts, backoff,
    # transient-vs-permanent, stale sweep), tracked in the summary_runs table.
    summary_max_attempts: int = 3  # after this many claims, mark permanently Failed
    # Much longer base than ingest_retry_base_seconds (2.0) on purpose: a summary is a
    # minutes-long LLM job over a whole lecture, so retrying seconds later just stacks work on a
    # backend that is probably still busy or still rate-limited.
    summary_retry_base_seconds: float = 30.0  # exponential: 30s, 60s, 120s
    summary_retry_max_seconds: float = 300.0  # backoff cap
    # A Running run older than this is presumed dead (the process was killed mid-generation) and
    # is reset to Pending. Without it the row stays Running forever and, since Running is not
    # claimable, is never retried.
    summary_stale_minutes: int = 15
    summary_retry_poll_seconds: float = 30.0  # how often the retry sweep looks for due runs
    summary_retry_batch_size: int = 10  # max due runs claimed per sweep

    # Transactional outbox. The ready/failure message is written in the SAME transaction that
    # marks the run terminal, then drained by a relay — so a broker outage can no longer discard
    # a summary that was already generated, rendered and uploaded (which is what happened on
    # 2026-07-30, costing a full Gemini run).
    outbox_poll_seconds: float = 5.0  # relay poll interval when the table is empty
    outbox_batch_size: int = 20  # messages published per pass
    outbox_max_attempts: int = 0  # 0 = retry forever; a message must never be silently dropped

    # AMQP consumer for SessionSummaryRequestedMessage from ClassroomService. Replaces the
    # synchronous HTTP trigger, which silently lost the request whenever this service was
    # unreachable at session end.
    summary_consumer_enabled: bool = True
    summary_consumer_queue: str = "knowledge-service-summary-requested"
    summary_consumer_prefetch: int = 4

    # RabbitMQ, for publishing the SessionSummaryReadyMessage to the MassTransit bus.
    # Defaults match the platform compose broker; only the live publisher uses these.
    # The host is the broker's container_name, which is intellilect-mq — NOT "rabbitmq".
    # It read "rabbitmq" until 2026-07-30, so a summary could generate and upload and then
    # fail to publish with a bare DNS error, leaving the classroom stuck showing no summary.
    rabbitmq_host: str = "intellilect-mq"
    rabbitmq_port: int = 5672
    rabbitmq_username: str = "jafar.mahmood"
    rabbitmq_password: str = "Jafar123!"
    rabbitmq_vhost: str = "/"

    # --- Observability (Phase 9) ---
    log_level: str = "INFO"  # root log level (DEBUG/INFO/WARNING/...)
    metrics_enabled: bool = True  # expose /metrics and record Prometheus metrics

    # --- Internal API security ---
    internal_api_secret: str = ""

    # --- S3 / object storage (placeholders, unused for now) ---
    s3_bucket_name: str = ""
    s3_service_url: str = ""
    s3_access_key: str = ""
    s3_secret_key: str = ""
    s3_region: str = "us-east-1"


@lru_cache
def get_settings() -> Settings:
    return Settings()  # type: ignore[call-arg]  # values come from the environment
