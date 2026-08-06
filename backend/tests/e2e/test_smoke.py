"""Smoke: the shortest sequence that proves a deployment is alive (work-plan §10.1).

    cd backend && docker compose up -d
    cd tests/e2e && ./run-in-network.sh -m smoke

Two halves, in the order that makes a failure quickest to read. First every service is
probed, so "the platform is broken" is answered before anything complicated runs.
Then the one cross-service journey that cannot pass unless the wiring is right: log in,
read a classroom, start a session — which cascades ClassroomService → StreamingService
→ LiveKit → LiveAssistantService.

**This suite deliberately does not wait for the platform.** The session-scoped readiness
gate in `conftest.py` polls for two minutes, which is right for a functional suite and
wrong here: the question a smoke run answers is "is this deployment alive *now*", and a
suite that waits turns a dead service into a slow pass. Set `E2E_SMOKE_WAIT_S` if you
are running it against a stack that is still coming up.

The old readiness gate is also why several of these probes are new. It polled the
gateway's `/health`, which nginx answers with a hard-coded 200 — so "the platform is
ready" meant "nginx is running", plus LiveAssistant and Rag. UserManagementService,
ClassroomService, StreamingService and EmailService were never checked by anything.
"""

from __future__ import annotations

import logging
import os
import time
from collections.abc import Iterator
from dataclasses import dataclass, field
from datetime import UTC, datetime, timedelta

import httpx
import pytest

from clients.classroom import ClassroomClient
from clients.ums import Account, UmsClient
from config import Config
from support import inventory

logger = logging.getLogger("e2e.smoke")

pytestmark = pytest.mark.smoke

# "Well under a minute" (§10.1). The probes are milliseconds; the session-start cascade
# is the whole cost, and a deployment where starting a session takes most of a minute is
# not a healthy one — so the budget is an assertion, not a comment.
BUDGET_S = float(os.environ.get("E2E_SMOKE_BUDGET_S", "60"))
PROBE_TIMEOUT_S = float(os.environ.get("E2E_SMOKE_PROBE_TIMEOUT_S", "5"))
WAIT_S = float(os.environ.get("E2E_SMOKE_WAIT_S", "0"))


@pytest.fixture(scope="session", autouse=True)
def platform_ready(config: Config) -> None:
    """Replaces conftest's two-minute poll. See the module docstring."""
    if WAIT_S <= 0:
        return
    deadline = time.monotonic() + WAIT_S
    while time.monotonic() < deadline:
        try:
            if httpx.get(f"{config.user_url}/health", timeout=PROBE_TIMEOUT_S).is_success:
                return
        except Exception:  # noqa: BLE001 — still starting
            pass
        time.sleep(2)


@dataclass
class Timings:
    """What each phase cost, so a slow smoke run names its own long pole."""

    started_at: float = field(default_factory=time.monotonic)
    phases: dict[str, float] = field(default_factory=dict)

    def record(self, phase: str, seconds: float) -> None:
        self.phases[phase] = seconds

    @property
    def elapsed(self) -> float:
        return time.monotonic() - self.started_at


@pytest.fixture(scope="module")
def timings() -> Iterator[Timings]:
    recorded = Timings()
    yield recorded
    for phase, seconds in recorded.phases.items():
        logger.info("smoke phase %-24s %6.2fs", phase, seconds)
    logger.info("smoke total %28.2fs (budget %.0fs)", recorded.elapsed, BUDGET_S)


def _probe(url: str) -> httpx.Response:
    return httpx.get(url, timeout=PROBE_TIMEOUT_S, follow_redirects=True)


# --- half one: is every service answering? ------------------------------------------


@pytest.mark.parametrize("probe", inventory.PROBES, ids=lambda p: p.service)
def test_service_answers_its_health_endpoint(probe: inventory.Probe, config: Config) -> None:
    url = getattr(config, probe.url_env).rstrip("/") + probe.path

    response = _probe(url)

    # 2xx only. A 503 means the endpoint works and the service is broken, which is a
    # *successful probe* — and exactly the case that could not happen before §10.1 gave
    # three of these services a health check capable of failing.
    assert response.is_success, (
        f"{probe.service} ({probe.description}) answered {response.status_code} at {url}: "
        f"{response.text[:400]}"
    )


def test_email_service_answers(config: Config) -> None:
    # EmailService publishes no host port and nginx does not route to it, so this only
    # works in-network. Skipped rather than silently dropped: bulk approve fans out
    # through it, and a smoke run that never touches it should say so.
    url = os.environ.get("E2E_EMAIL_URL") or inventory.IN_NETWORK_ONLY["email-service"]
    try:
        response = _probe(url)
    except httpx.HTTPError as exc:
        pytest.skip(f"email-service unreachable from here ({exc}); run in-network")

    assert response.is_success, f"email-service answered {response.status_code} at {url}"


def test_object_storage_answers(config: Config) -> None:
    scheme = "https" if config.minio_secure else "http"
    url = f"{scheme}://{config.minio_endpoint}{inventory.INFRASTRUCTURE_PROBES['intellilect-s3']}"

    assert _probe(url).is_success, f"object storage not live at {url}"


def test_the_gateway_answers(config: Config) -> None:
    # Worth keeping and worth not over-reading: nginx returns a hard-coded 200 here, so
    # this proves the gateway process is up and routes nothing else. It is listed as its
    # own probe precisely so nobody mistakes it for a platform check again.
    assert _probe(f"{config.gateway_url}/health").is_success


def test_the_media_server_accepts_connections(config: Config) -> None:
    url = config.livekit_ws_url.replace("ws://", "http://").replace("wss://", "https://")
    try:
        response = _probe(url)
    except httpx.HTTPError as exc:
        pytest.skip(f"livekit-server unreachable from here ({exc})")

    # Any answer at all. LiveKit publishes no health route on the signalling port, and
    # "the process accepted a TCP connection and spoke HTTP" is the whole claim.
    assert response.status_code < 500, f"livekit-server answered {response.status_code}"


# --- half two: does the platform still do its job? ----------------------------------


def test_a_seeded_account_can_log_in(ums: UmsClient, config: Config, timings: Timings) -> None:
    """The one probe that cannot be faked by a process being up.

    UserManagementService's `/health` now gates on its database, but login also needs
    the seeder to have run, the password hasher to be configured and the JWT signing key
    to be present — none of which liveness can see.
    """
    started_at = time.monotonic()
    account = ums.login_account(
        Account(user_id="", email=config.admin_email, password=config.admin_password, role="Admin")
    )
    timings.record("login", time.monotonic() - started_at)

    assert account.access_token, "login returned no access token"


def test_a_teacher_can_read_their_classrooms(
    make_user, classroom: ClassroomClient, timings: Timings
) -> None:
    started_at = time.monotonic()
    teacher = make_user("Teacher", "smoke-teacher")
    classroom_id = classroom.create_classroom(teacher, name="Smoke", description="§10.1")
    sessions = classroom.get_sessions(teacher, classroom_id)
    timings.record("classroom read/write", time.monotonic() - started_at)

    # Registration, admin approval, login, a write and a read — the whole account
    # lifecycle, which is what most of a broken deployment fails somewhere inside.
    assert sessions == [], "a brand-new classroom already has sessions"


def test_a_session_starts_and_ends(
    make_user, classroom: ClassroomClient, timings: Timings
) -> None:
    """The cascade, which is the only part of this suite that spans four services.

    Starting a session makes ClassroomService call StreamingService, which creates the
    LiveKit room and notifies LiveAssistantService. Nothing short of doing it proves
    those seams are wired, and it is the step that breaks when a URL or an internal
    secret drifts between environments.
    """
    teacher = make_user("Teacher", "smoke-cascade")
    classroom_id = classroom.create_classroom(teacher, name="Smoke cascade", description="§10.1")
    session_id = classroom.create_session(
        teacher,
        classroom_id,
        title="Smoke session",
        scheduled_at_utc=(datetime.now(UTC) + timedelta(minutes=1)).isoformat(),
    )

    started_at = time.monotonic()
    try:
        classroom.start_session(teacher, classroom_id, session_id)
        timings.record("session start (cascade)", time.monotonic() - started_at)
    finally:
        # Always. A smoke run that left a live session behind would leak a LiveKit room
        # and a running agent pipeline on every invocation — and smoke is the suite most
        # likely to be run repeatedly against a real deployment.
        ended_at = time.monotonic()
        outcome = classroom.end_session(teacher, classroom_id, session_id)
        timings.record("session end", time.monotonic() - ended_at)
        logger.info("session end outcome: %s", outcome)


# --- the budget ---------------------------------------------------------------------


def test_the_whole_smoke_finished_well_under_a_minute(timings: Timings) -> None:
    """§10.1 says "well under a minute", so that is an assertion.

    Not pedantry about test duration: this suite is the thing you run to decide whether
    a deployment is usable, and it is only useful if running it is cheaper than looking.
    A smoke suite that takes two minutes stops being run, and then it stops being true.
    """
    if not timings.phases:
        pytest.skip("no phases ran — run the whole module, not a single test")

    slowest = max(timings.phases.items(), key=lambda item: item[1])
    assert timings.elapsed < BUDGET_S, (
        f"smoke took {timings.elapsed:.1f}s against a {BUDGET_S:.0f}s budget; "
        f"the long pole was {slowest[0]} at {slowest[1]:.1f}s"
    )
