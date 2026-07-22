"""Fixtures for the cross-service E2E: config, service clients, platform readiness.

The suite talks to a *running* platform (`docker compose up -d` from `backend/`).
Nothing here starts containers — see README.md for the runbook. Health is polled so
a freshly-started stack is waited on rather than failing instantly.
"""

from __future__ import annotations

import logging
from collections.abc import Callable, Iterator

import httpx
import pytest

from clients.classroom import ClassroomClient
from clients.knowledge import KnowledgeClient
from clients.liveassistant import LiveAssistantClient
from clients.streaming import StreamingClient
from clients.ums import Account, UmsClient
from config import CONFIG, Config
from support.ids import unique_email, unique_username
from support.waiting import poll_until

logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(name)s: %(message)s")
logger = logging.getLogger("e2e")


@pytest.fixture(scope="session")
def config() -> Config:
    return CONFIG


@pytest.fixture(scope="session")
def ums(config: Config) -> Iterator[UmsClient]:
    client = UmsClient(config.user_url, config.http_timeout_s)
    yield client
    client.close()


@pytest.fixture(scope="session")
def classroom(config: Config) -> Iterator[ClassroomClient]:
    client = ClassroomClient(config.classroom_url, config.http_timeout_s)
    yield client
    client.close()


@pytest.fixture(scope="session")
def streaming(config: Config) -> Iterator[StreamingClient]:
    client = StreamingClient(config.streaming_url, config.http_timeout_s)
    yield client
    client.close()


@pytest.fixture(scope="session")
def liveassistant(config: Config) -> Iterator[LiveAssistantClient]:
    client = LiveAssistantClient(
        config.liveassistant_url, config.internal_secret, config.http_timeout_s
    )
    yield client
    client.close()


@pytest.fixture(scope="session")
def knowledge(config: Config) -> Iterator[KnowledgeClient]:
    client = KnowledgeClient(
        config.knowledge_url, config.internal_secret, config.http_timeout_s
    )
    yield client
    client.close()


def _http_ok(url: str) -> bool:
    try:
        return httpx.get(url, timeout=5).status_code < 500
    except Exception:  # noqa: BLE001
        return False


@pytest.fixture(scope="session", autouse=True)
def platform_ready(config: Config, liveassistant: LiveAssistantClient, knowledge: KnowledgeClient) -> None:
    """Block until the HTTP services answer. LiveKit is NOT a hard gate here — it is
    only needed by the media test, which verifies it by actually connecting; its
    node-ip may be unreachable for a plain HTTP health probe (VPN/host routing)."""

    def ready() -> bool:
        checks = {
            "gateway": _http_ok(f"{config.gateway_url}/health"),
            "liveassistant": liveassistant.healthy(),
            "knowledge": knowledge.healthy(),
        }
        down = [name for name, ok in checks.items() if not ok]
        if down:
            logger.info("Waiting for services: %s", ", ".join(down))
        return not down

    poll_until(
        ready,
        timeout_s=config.platform_ready_timeout_s,
        interval_s=3.0,
        description="platform services to become healthy",
    )
    livekit_http = config.livekit_ws_url.replace("ws://", "http://").replace("wss://", "https://")
    if not _http_ok(livekit_http):
        logger.warning("LiveKit HTTP probe at %s failed — media test may skip.", livekit_http)
    logger.info("Platform is ready.")


@pytest.fixture(scope="session")
def admin(ums: UmsClient, config: Config) -> Account:
    """The seeded Admin account, logged in — used to approve new registrations."""
    account = Account(user_id="", email=config.admin_email, password=config.admin_password, role="Admin")
    return ums.login_account(account)


@pytest.fixture(scope="session")
def make_user(
    ums: UmsClient, admin: Account, config: Config
) -> Callable[[str, str], Account]:
    """Factory: register a Teacher/Student, admin-approve it, log it in."""
    role_ids = ums.registration_role_ids()

    def _make(role_name: str, label: str) -> Account:
        role_id = role_ids.get(role_name)
        assert role_id, f"role '{role_name}' not in registration roles {list(role_ids)}"
        account = ums.register(
            username=unique_username(label),
            email=unique_email(label),
            first_name=label.capitalize(),
            last_name="E2E",
            role_id=role_id,
            password=config.user_password,
            role_name=role_name,
        )
        ums.approve(admin, account.user_id)
        ums.login_account(account)
        logger.info("Provisioned %s %s (%s)", role_name, account.email, account.user_id)
        return account

    return _make
