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

    # --- Ollama embeddings (local, HTTP-only — no model weights in the container) ---
    # On Linux, host.docker.internal resolves to the host via the compose
    # `extra_hosts: host-gateway` mapping; host Ollama must listen on 0.0.0.0:11434.
    ollama_base_url: str = "http://host.docker.internal:11434"
    # Optional bearer token. Sent as `Authorization: Bearer <token>` ONLY if set.
    ollama_auth_token: str = ""
    embedding_model: str = "qwen3-embedding"
    embedding_dim: int = 1024
    embedding_timeout_seconds: float = 60.0
    # Retrieval instruction prepended to QUERIES only (documents are embedded raw).
    # Must contain a `{query}` placeholder. Improves asymmetric query/passage recall.
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

    # --- Ingestion worker (Phase 5) ---
    ingest_max_concurrency: int = 1  # concurrent worker tasks — the RAM cap
    ingest_queue_max: int = 100  # bounded in-process job queue size
    embed_batch_size: int = 32  # chunk texts per embed_documents call

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
    # Local generative model in host Ollama (reuses OLLAMA_BASE_URL / OLLAMA_AUTH_TOKEN).
    generation_model: str = "qwen2.5:7b-instruct"
    generation_timeout_seconds: float = 120.0
    generation_temperature: float = 0.2
    generation_max_tokens: int = 1024  # num_predict
    answer_top_k: int = 6  # chunks retrieved for answer context
    context_max_tokens: int = 6000  # token budget for the packed context block

    # --- Session summary (S-1) ---
    # Turns a lecture transcript (fetched from LiveAssistantService) into a structured
    # Markdown summary. Reuses the local Ollama generative model (OLLAMA_BASE_URL /
    # OLLAMA_AUTH_TOKEN) but with its own generation parameters so summarization can be
    # tuned independently of answering. This phase STOPS at Markdown (PDF is S-2).
    summary_model: str = "qwen2.5:7b-instruct"  # reuse the generation model by default
    summary_temperature: float = 0.3
    summary_max_tokens: int = 1500  # num_predict for a summary pass
    summary_grounding_enabled: bool = True  # ground key terms in classroom material
    summary_grounding_top_k: int = 6  # chunks retrieved as supporting context
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

    # RabbitMQ, for publishing the SessionSummaryReadyMessage to the MassTransit bus.
    # Defaults match the platform compose broker; only the live publisher uses these.
    rabbitmq_host: str = "rabbitmq"
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
