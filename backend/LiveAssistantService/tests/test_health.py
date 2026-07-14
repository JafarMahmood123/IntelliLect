from __future__ import annotations

import pytest
from fastapi.testclient import TestClient

from app.api.main import create_app
from app.infrastructure.config.settings import get_settings

_LIVEKIT_ENV = ("LIVEKIT_URL", "LIVEKIT_API_KEY", "LIVEKIT_API_SECRET")


@pytest.fixture(autouse=True)
def _clear_settings_cache():
    """Settings are cached; clear before and after so env changes take effect."""
    get_settings.cache_clear()
    yield
    get_settings.cache_clear()


def test_health_ok_and_livekit_not_configured(monkeypatch):
    for key in _LIVEKIT_ENV:
        monkeypatch.delenv(key, raising=False)
    get_settings.cache_clear()

    response = TestClient(create_app()).get("/health")

    assert response.status_code == 200
    assert response.json() == {"status": "ok", "livekit": "not-configured"}


def test_health_reports_livekit_configured(monkeypatch):
    monkeypatch.setenv("LIVEKIT_URL", "ws://livekit:7880")
    monkeypatch.setenv("LIVEKIT_API_KEY", "devkey")
    monkeypatch.setenv("LIVEKIT_API_SECRET", "devsecret")
    get_settings.cache_clear()

    response = TestClient(create_app()).get("/health")

    assert response.status_code == 200
    assert response.json() == {"status": "ok", "livekit": "configured"}
