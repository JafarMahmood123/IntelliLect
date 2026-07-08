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

    # --- Gemini embeddings ---
    gemini_api_key: str = ""
    embedding_model: str = "gemini-embedding-001"
    embedding_dim: int = 768

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
