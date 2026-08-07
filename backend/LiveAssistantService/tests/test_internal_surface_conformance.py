"""Every route this service serves, against the two things that keep it private (work-plan §7.13b).

This service holds no user tokens. The shared secret is the whole of its authorization, and the
gateway not routing to it is the whole of its exposure — so those are the two facts worth pinning,
and neither was checked by anything.

**The guard was a habit repeated per endpoint.** Four routers, each with
`dependencies=[Depends(require_internal_secret)]`, and a test per endpoint asserting a 401 without
it. That is the same shape ClassroomService had before §11.2: correct today, and with nothing
anywhere that would notice the one that forgot. Written here over the **mounted app** rather than
over the source, because `dependencies=` on an `APIRouter` only protects the routes actually
included from it — a router mounted twice, or a route added to `app` directly, carries no such
guarantee and reads identically in the file.

**And `NginxRouteTableTests` cannot see this service at all.** It resolves every `[Route]` attribute
in the three .NET assemblies against `nginx.conf`; the two Python services are simply outside its
reach. What protects them is that nginx declares no `location` for either, so everything they serve
falls into `location /api/` and lands on UserManagementService, which answers 404. That is safety by
placement, exactly as B-15 says of the .NET internal controllers — and it is one `location` block
away from ending.

Every route here is under `/api/internal`, which makes this service the easier of the two to reason
about; RagService serves `/api/search` and `/api/answer`, which are internal surfaces that do not
look like one. The rule is the same either way, because the naming was never what protected them.

Duplicated from RagService, following `test_migration_conformance.py`: the two services share no
test library and no virtualenv, and a rule that only one of them runs is a rule that half the
surface has.
"""

from __future__ import annotations

import re
from pathlib import Path

import pytest
from fastapi.routing import APIRoute
from fastapi.testclient import TestClient

from app.api.dependencies import require_internal_secret
from app.api.main import create_app

BACKEND = Path(__file__).resolve().parents[2]
NGINX = BACKEND / "nginx.conf"

#: This service's hostname on the compose network, as nginx would have to name it to reach here.
SERVICE_HOSTNAME = "live-assistant-service"

#: Paths deliberately served without the shared secret, each with the reason. Checked in both
#: directions below, so an entry that stops being true fails rather than rots.
UNGUARDED: dict[str, str] = {
    "/health": "liveness, read by compose and by the §10.1 smoke inventory before any secret exists",
    "/metrics": "Prometheus scrape on the internal network; carries counters, never content",
}
# Kept identical to RagService's: the two services expose the same two unguarded paths, and a
# difference between these lists should be a decision rather than drift.


def _walk(routes) -> list[APIRoute]:
    """Every APIRoute the app actually serves, including those inside included routers.

    `app.routes` is not a flat list. FastAPI 0.141 wraps each `include_router` call in an
    `_IncludedRouter` that holds the original router rather than copying its routes up, so the
    obvious `[r for r in app.routes if isinstance(r, APIRoute)]` returns **nothing** — which is
    exactly what the first run of this file did, and what the vacuum guard at the bottom caught.
    A rule that silently sees no routes is worse than no rule.
    """
    found: list[APIRoute] = []
    for route in routes:
        included = getattr(route, "original_router", None)
        if included is not None:
            found.extend(_walk(included.routes))
        elif isinstance(route, APIRoute):
            found.append(route)
    return found


def _routes() -> list[APIRoute]:
    return _walk(create_app().routes)


def _is_guarded(route: APIRoute) -> bool:
    """Whether the secret guard actually runs for this route, as FastAPI resolved it."""
    return any(
        dependency.call is require_internal_secret
        for dependency in route.dependant.dependencies
    )


# --- the guard ----------------------------------------------------------------------------------


def test_every_route_is_guarded_or_is_a_named_exception() -> None:
    open_routes = sorted(
        {route.path for route in _routes() if not _is_guarded(route)} - set(UNGUARDED)
    )

    assert not open_routes, (
        "These routes are served without the shared secret and are not on the exemption list, so "
        f"anything that can reach this container may call them: {open_routes}"
    )


def test_no_exemption_names_a_route_that_no_longer_exists() -> None:
    # A stale entry excuses nothing today and excuses whatever later takes the path — the same
    # failure the .NET exemption lists guard against in both directions.
    present = {route.path for route in _routes()}
    stale = sorted(set(UNGUARDED) - present)

    assert not stale, f"Exempted but no such route: {stale}"


def test_no_exemption_names_a_route_that_has_since_been_guarded() -> None:
    # The direction that lets a list rot quietly: a path that gained the guard should come off, or
    # the list stops meaning "these are the unguarded ones".
    unnecessary = sorted(
        route.path for route in _routes() if route.path in UNGUARDED and _is_guarded(route)
    )

    assert not unnecessary, f"Exempted but now guarded — remove from the list: {unnecessary}"


def test_the_guard_fails_closed_when_no_secret_is_configured() -> None:
    """An unconfigured secret must refuse everything, not admit everything.

    §7b found the opposite on the .NET side: the guard there compared the header to a missing
    setting and let the request through. The Python guard is written correctly — `if not expected
    or ...` — and until now nothing pinned it, so the correctness was a reading of the code rather
    than a fact about it.
    """
    app = create_app()
    with TestClient(app) as client:
        guarded = next(route.path for route in _routes() if _is_guarded(route))

        # No secret presented, against a server whose own secret is blank.
        from app.api.dependencies import get_settings
        from app.infrastructure.config.settings import Settings

        app.dependency_overrides[get_settings] = lambda: Settings(internal_api_secret="")
        try:
            response = client.post(guarded, json={})
        finally:
            app.dependency_overrides.pop(get_settings, None)

    assert response.status_code == 401, (
        f"{guarded} answered {response.status_code} with no secret configured on either side; an "
        "unconfigured guard must refuse rather than admit."
    )


# --- the gateway --------------------------------------------------------------------------------


def _nginx_upstreams() -> dict[str, str]:
    config = NGINX.read_text()
    return {
        match.group(1): match.group(2)
        for match in re.finditer(
            r"upstream\s+([A-Za-z0-9_.-]+)\s*\{\s*server\s+([A-Za-z0-9_.-]+):", config
        )
    }


def _nginx_locations() -> dict[str, str]:
    config = NGINX.read_text()
    found: dict[str, str] = {}
    for match in re.finditer(r"location\s+([^\s{]+)\s*\{(.*?)\n\s*\}", config, re.DOTALL):
        proxy = re.search(r"proxy_pass\s+https?://([A-Za-z0-9_.-]+)", match.group(2))
        if proxy:
            found[match.group(1)] = proxy.group(1)
    return found


def _upstream_for(path: str) -> str | None:
    """Where nginx would send `path` — longest matching prefix wins, as nginx resolves it."""
    upstreams = _nginx_upstreams()
    best = max(
        (location for location in _nginx_locations() if path.startswith(location)),
        key=len,
        default=None,
    )
    return upstreams.get(_nginx_locations()[best]) if best else None


def test_no_route_this_service_serves_is_reachable_through_the_gateway() -> None:
    exposed = sorted(
        route.path for route in _routes() if _upstream_for(route.path) == SERVICE_HOSTNAME
    )

    assert not exposed, (
        "These routes are proxied from outside straight to this service, so the shared secret is "
        f"the only thing between the public internet and them: {exposed}"
    )


def test_the_rule_can_see_a_location_that_would_expose_this_service() -> None:
    # The hazard exercised rather than described. The rule above passes today and would pass
    # whether or not it worked, because nginx names no upstream for this service at all — so
    # without this, a resolver that always returned None would look identical to a correct one.
    locations = _nginx_locations()
    upstreams = _nginx_upstreams()

    assert locations, "No nginx locations parsed — the rule above is vacuous."
    assert upstreams, "No nginx upstreams parsed — the rule above is vacuous."

    # Every location nginx declares resolves to a real upstream, and each of those names a service
    # that is not this one. If a `location /api/search` were ever added for rag-service, the set
    # below would contain this service's hostname and the rule above would fail.
    resolved = {upstreams.get(target) for target in locations.values()}
    assert None not in resolved, (
        f"nginx proxies to an upstream it never declares: {locations} vs {upstreams}"
    )
    assert SERVICE_HOSTNAME not in resolved, (
        "nginx now declares an upstream for this service; every route it serves is a candidate for "
        "public exposure and the exemption reasoning above needs revisiting."
    )


def test_everything_this_service_serves_lands_on_a_service_that_does_not_host_it() -> None:
    # The other half of the same fact, and the one that explains WHY this is safe today: these
    # paths do resolve — to UserManagementService, via `location /api/`, which has no such route
    # and answers 404. Safe by placement rather than by policy, which is what makes the rule worth
    # keeping.
    for route in _routes():
        if route.path in UNGUARDED:
            continue
        assert _upstream_for(route.path) != SERVICE_HOSTNAME, route.path


# --- guards on the guards -----------------------------------------------------------------------


def test_there_are_routes_and_a_config_to_check() -> None:
    # Every rule above is a query, and a query matching nothing passes loudly while proving
    # nothing. `create_app()` returning an app with no routers mounted, or the nginx file moving,
    # would make this file decorative without a single failure.
    routes = _routes()
    assert len(routes) >= 10, f"Only found {len(routes)} routes."
    assert NGINX.exists(), f"nginx.conf not found at {NGINX}"

    guarded = [route for route in routes if _is_guarded(route)]
    assert len(guarded) >= 8, f"Only {len(guarded)} routes carry the secret guard."

    # And that `_is_guarded` can say no — otherwise the first rule passes for every input.
    assert any(not _is_guarded(route) for route in routes)


@pytest.mark.parametrize("path", sorted(UNGUARDED))
def test_each_exemption_is_reachable_without_a_secret(path: str) -> None:
    # The list says these answer without the shared secret. Asserted rather than assumed, because
    # an exemption for a route that actually requires the secret is a claim nobody would check —
    # and the smoke inventory (§10.1) depends on exactly these answering before any secret exists.
    with TestClient(create_app()) as client:
        assert client.get(path).status_code != 401
