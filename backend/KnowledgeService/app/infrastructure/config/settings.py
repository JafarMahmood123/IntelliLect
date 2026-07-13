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
