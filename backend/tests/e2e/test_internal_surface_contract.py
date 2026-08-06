"""The `/api/internal` surface refuses anyone without the shared secret — work-plan §8.7.

These routes carry no user token. There is no `[Authorize]`, no role check, no classroom
membership check on any of them — the `X-Internal-Secret` header is the entire authorization,
across five services. That makes this the one contract where a single omission is not a
degraded feature but an open door: `/api/internal/classrooms` lists every classroom on the
platform, `/api/internal/documents` lists every uploaded file, and
`/api/internal/sessions/{id}/transcript` returns the verbatim text of a lecture.

Unit tests already cover the guard's *logic* — the filter in the two .NET services, the FastAPI
dependency in the two Python ones, plus a rule over each assembly asserting every internal
controller carries it (test-plan B-08/B-09/B-12/B-13). What no unit test can prove is that the
guard is actually *mounted* on the running service: a router registered without its dependency,
a filter attribute lost in a refactor, or a reverse proxy that strips the header all look
identical to a passing unit test. That is what this file is for, and it is why it belongs at
the integration level (test-plan B-10).

## Run it in-network

    ./run-in-network.sh -m internal

Through the gateway these routes are blocked by nginx, so a test that reached them via
`E2E_GATEWAY_URL` would be testing nginx's route table and would pass with the guard entirely
removed. The in-network runner points each client at the service's own container DNS name,
which is exactly how the services reach each other in production — the path the guard exists to
protect.

## Only GET routes are probed, deliberately

The internal surface includes deletes: purging a classroom's index, dropping a session
transcript, removing documents. If the guard were broken — the very thing this file exists to
detect — a probe of those would carry it out. So every route below is read-only. A guard that
protects the reads on a controller protects its writes too: the .NET filter is declared at the
controller level and the FastAPI dependency at the router level, both covering every method,
and there are unit-level rules over each assembly pinning that. The read is a safe proxy for
the whole controller.

Two routers are therefore **not** probed here at all, because they carry no read:
`RagService/internal_summaries.py` and `LiveAssistantService/internal_quizzes.py` are POST-only.
Their guard is covered at the unit level by the same router/assembly rules, and probing them
would mean triggering a summary run and a quiz generation on every test run — real model calls,
really billed. Worth stating rather than leaving as an apparent omission.
"""

from __future__ import annotations

from dataclasses import dataclass

import httpx
import pytest

from config import Config

pytestmark = pytest.mark.internal


@dataclass(frozen=True)
class InternalRoute:
    """One read-only route on the internal surface."""

    service: str
    #: Attribute on Config holding that service's base URL.
    url_attr: str
    path: str

    def __str__(self) -> str:  # pytest's parametrize id
        return f"{self.service}{self.path}"


# Derived from the routers/controllers themselves, not from documentation:
#   ClassroomService  Presentation/Controllers/Internal*.cs
#   StreamingService  Presentation/Controllers/InternalStreamsController.cs
#   RagService        app/api/routers/internal_*.py
#   LiveAssistantService  app/api/routers/internal_*.py
#
# A route that needs an id uses a random-looking one on purpose: the assertion is about the
# guard, which runs BEFORE the handler, so whether the id exists is irrelevant. What matters is
# that an unauthenticated caller cannot tell the difference either — see the 404 note below.
MISSING_ID = "00000000-0000-0000-0000-000000000001"

ROUTES: list[InternalRoute] = [
    # --- ClassroomService (unpublished port; reachable in-network only) ---
    InternalRoute("classroom-service", "classroom_url", "/api/internal/classrooms"),
    InternalRoute("classroom-service", "classroom_url", f"/api/internal/classrooms/{MISSING_ID}"),
    InternalRoute("classroom-service", "classroom_url", f"/api/internal/classrooms/{MISSING_ID}/members"),
    InternalRoute("classroom-service", "classroom_url", f"/api/internal/classrooms/{MISSING_ID}/deletion-impact"),
    InternalRoute("classroom-service", "classroom_url", "/api/internal/files"),
    InternalRoute("classroom-service", "classroom_url", "/api/internal/sessions"),
    InternalRoute("classroom-service", "classroom_url", f"/api/internal/sessions/{MISSING_ID}/deletion-impact"),
    InternalRoute("classroom-service", "classroom_url", "/api/internal/outputs"),
    InternalRoute("classroom-service", "classroom_url", f"/api/internal/users/{MISSING_ID}/classrooms"),
    # --- StreamingService ---
    InternalRoute("streaming-service", "streaming_url", "/api/internal/streams/live"),
    # --- RagService ---
    InternalRoute("rag-service", "knowledge_url", "/api/internal/documents"),
    InternalRoute("rag-service", "knowledge_url", f"/api/internal/documents/{MISSING_ID}/status"),
    InternalRoute("rag-service", "knowledge_url", f"/api/internal/documents/{MISSING_ID}/detail"),
    InternalRoute("rag-service", "knowledge_url", "/api/internal/knowledge/stats"),
    InternalRoute("rag-service", "knowledge_url", "/api/internal/reembed/status"),
    # --- LiveAssistantService ---
    InternalRoute("live-assistant-service", "liveassistant_url", "/api/internal/sessions"),
    InternalRoute("live-assistant-service", "liveassistant_url", f"/api/internal/sessions/{MISSING_ID}/transcript"),
    InternalRoute("live-assistant-service", "liveassistant_url", f"/api/internal/sessions/{MISSING_ID}/feedback"),
]


def _get(config: Config, route: InternalRoute, headers: dict[str, str]) -> httpx.Response:
    base = getattr(config, route.url_attr)
    return httpx.get(f"{base}{route.path}", headers=headers, timeout=config.http_timeout_s)


@pytest.mark.parametrize("route", ROUTES, ids=str)
def test_no_secret_is_refused(config: Config, route: InternalRoute) -> None:
    """No header at all — the shape of a request that reached the service from outside."""
    response = _get(config, route, headers={})

    assert response.status_code == 401, (
        f"{route} answered {response.status_code} with NO internal secret. "
        "These routes have no other authorization: this is an open door."
    )


@pytest.mark.parametrize("route", ROUTES, ids=str)
def test_a_wrong_secret_is_refused(config: Config, route: InternalRoute) -> None:
    """A header that is present but wrong.

    Separate from the missing-header case because they fail on different branches: one is
    "was anything sent", the other is "does it match". A guard that only checked presence
    would pass the test above and fail this one.
    """
    response = _get(config, route, headers={"X-Internal-Secret": "not-the-secret"})

    assert response.status_code == 401, (
        f"{route} answered {response.status_code} with a WRONG internal secret."
    )


@pytest.mark.parametrize("route", ROUTES, ids=str)
def test_the_correct_secret_is_admitted(config: Config, route: InternalRoute) -> None:
    """The anti-vacuity half, and the reason the two tests above mean anything.

    A service that is down, a path that no longer exists, or a guard that rejects every
    request would make both refusal tests pass for entirely the wrong reason — and they are
    the tests most likely to be trusted without being re-read.

    The assertion is deliberately weak on the success side: **not 401**. A missing id gives
    404, an empty list gives 200, and the deletion-impact routes may 404 or 200 depending on
    what is seeded. Pinning a specific status here would make this file break whenever the
    fixture data changed, which is how a security test ends up being deleted.
    """
    response = _get(config, route, headers=config.internal_headers())

    assert response.status_code != 401, (
        f"{route} refused the CORRECT internal secret ({response.status_code}). "
        "Either the secret is misconfigured across services, or the guard rejects everything "
        "— in which case the two refusal tests above are passing vacuously."
    )


def test_every_service_with_an_internal_surface_is_covered() -> None:
    """A rule over the table itself.

    The routes are hand-listed from the source, so the risk this file carries is that a new
    internal controller is added and nobody adds it here — leaving the surface it exposes
    unprobed while the suite still reports green. This does not catch a new route on an
    existing service (nothing at this level can), but it does catch a whole service dropping
    out of the list, which is the version of the mistake that loses the most coverage at once.
    """
    covered = {route.service for route in ROUTES}

    assert covered == {
        "classroom-service",
        "streaming-service",
        "rag-service",
        "live-assistant-service",
    }
