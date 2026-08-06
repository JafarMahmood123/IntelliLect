"""Rules keeping the smoke suite honest (§10.1). Needs nothing running.

A smoke suite decays in a way its own results cannot show. Nobody writes a wrong
assertion into it — what happens is that a service is added to compose and never gets a
probe, and the suite goes on passing while proving less every release. The green tick
means "everything I still check is fine", and nothing says how much that is any more.

So the inventory is checked against the compose files rather than maintained by hand,
and both exemption lists are checked in both directions — same shape as
`PublicRouteAuthorizationTests` and `InternalSecretGuardTests`, and for the same reason.

The platform-readiness gate from conftest is shadowed below: nothing here connects.
"""

from __future__ import annotations

import pytest

from support import inventory

pytestmark = pytest.mark.smoke


@pytest.fixture(scope="session", autouse=True)
def platform_ready() -> None:
    """Shadows conftest's readiness poll — these rules read files, not sockets."""
    return None


@pytest.fixture(autouse=True)
def _sources_available() -> None:
    """In-network runs bind-mount only `tests/e2e`, so the sources are not there."""
    if not inventory.COMPOSE_FILES[0].exists():
        pytest.skip("run this on the host — the compose files are not mounted in-network")


# --- the inventory covers the platform ---------------------------------------------


def test_every_compose_service_is_either_probed_or_exempt_with_a_reason():
    uncovered = sorted(inventory.compose_services() - inventory.covered())

    assert not uncovered, (
        "These services are in the compose graph and the smoke suite says nothing about "
        f"them, so a deployment missing them still passes: {', '.join(uncovered)}"
    )


def test_nothing_is_listed_that_compose_no_longer_runs():
    # A probe for a deleted service is worse than none: it either fails forever and gets
    # deleted along with whatever else was in that commit, or — for an exemption — it
    # silently excuses the next service to take the name.
    stale = sorted(inventory.covered() - inventory.compose_services())

    assert not stale, f"Listed but not in compose: {', '.join(stale)}"


def test_every_exemption_gives_a_reason_rather_than_an_empty_string():
    # The list is only worth anything if being on it is a decision. An exemption with no
    # reason is indistinguishable from an omission somebody silenced.
    unexplained = sorted(name for name, why in inventory.EXEMPT.items() if not why.strip())

    assert not unexplained, f"Exempt with no reason given: {', '.join(unexplained)}"


def test_there_are_enough_services_for_these_rules_to_mean_anything():
    # A parse that silently returned nothing would make every rule above pass while
    # checking an empty set — the most comfortable way for a conformance test to die.
    services = inventory.compose_services()

    assert len(services) >= 15, f"Only found {len(services)} compose services."
    assert "user-service" in services and "livekit-server" in services


# --- every service actually exposes what the probes assume --------------------------


@pytest.mark.parametrize("service", sorted(inventory.DOTNET_HEALTH_SOURCES))
def test_every_dotnet_service_maps_a_health_endpoint(service: str):
    """The rule that would have caught the hole this section opened with.

    UserManagementService had no `/health` at all — the service that owns login, and so
    the one whose absence takes the whole platform down, was the only one nothing could
    probe. Nobody noticed because every *other* service had one, and a per-service habit
    is exactly the kind of thing that gets skipped once.
    """
    source = inventory.DOTNET_HEALTH_SOURCES[service].read_text(encoding="utf-8")

    # Both shapes count and nothing else does. Matching a bare "/health" string would
    # let a comment, a log line or a client call satisfy the rule — and this rule exists
    # precisely because an endpoint everyone assumed was there was not.
    mapped = 'MapHealthChecks("/health")' in source or 'MapGet("/health"' in source
    assert mapped, (
        f"{service} exposes no /health endpoint, so nothing outside it can tell whether "
        "it is alive — including the orchestrator that would restart it"
    )


@pytest.mark.parametrize("service", sorted(inventory.PYTHON_HEALTH_SOURCES))
def test_every_python_service_serves_a_health_route(service: str):
    source = inventory.PYTHON_HEALTH_SOURCES[service].read_text(encoding="utf-8")

    assert '@router.get("/health")' in source, f"{service} has no /health route"


@pytest.mark.parametrize("service", sorted(inventory.DATABASE_GATED))
def test_health_can_actually_fail_for_the_services_that_hold_data(service: str):
    """The distinction that decides whether `/health` is a probe or a formality.

    Every pre-existing health check in this solution returns `Degraded` when its
    dependency is missing, and `MapHealthChecks` answers Degraded with **200**. So before
    `DatabaseHealthCheck`, three of these endpoints could not return anything but success
    no matter what was broken — and the smoke suite's probe would have been asserting on
    a constant.
    """
    source = inventory.DATABASE_GATED[service].read_text(encoding="utf-8")

    assert "HealthCheckResult.Unhealthy" in source, (
        f"{service}'s database check never reports Unhealthy, so /health answers 200 with "
        "a dead database and the probe proves nothing"
    )
    assert "HealthCheckResult.Degraded" not in source, (
        f"{service}'s database check reports Degraded, which MapHealthChecks maps to 200"
    )
