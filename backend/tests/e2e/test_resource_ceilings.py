"""What a slow or absent dependency costs, as rules over configuration (§10.4). Nothing runs.

Behaviour under a *stopped* MinIO or Postgres needs containers, and stopping them is §10.4's
other half. But the things that decide that behaviour — whether a call has a timeout, how many
times it retries, and where its credentials come from — are configuration, and configuration can
be checked today.

Which is the more useful half, because both defects this file was written after were configuration
and neither was visible from inside the service that had them:

  * ClassroomService's MinIO credentials were **string literals** in the composition root, while
    MinIO itself and StreamingService's egress read the same two values from `backend/.env`.
    Everything worked until the MinIO password was changed — the first thing anyone does before a
    real deployment — and then every upload, download, recording and summary in that one service
    failed with `InvalidAccessKeyId` against a bucket that plainly exists.
  * StreamingService configured `LiveAssistant__BaseUrl` and no timeout, so the number lived in
    C# where nobody looks for it. Exactly the drift §14.3 fixed in UserManagementService.

The platform-readiness gate from conftest is shadowed below: nothing here connects.
"""

from __future__ import annotations

import re

import pytest

from support import inventory

pytestmark = pytest.mark.resilience

# The two variables the root compose file demands, and that MinIO itself is started with.
# Anything that talks to MinIO must read these and not a copy.
MINIO_USER_VAR = "MINIO_ROOT_USER"
MINIO_PASSWORD_VAR = "MINIO_ROOT_PASSWORD"

# The development MinIO credentials. They are in this file on purpose: it is the one place where
# naming them is the point, because the test is "these must appear nowhere else".
DEV_MINIO_CREDENTIALS = ("testuser", "testpassword123!")


@pytest.fixture(scope="session", autouse=True)
def platform_ready() -> None:
    """Shadows conftest's readiness poll — these rules read files, not sockets."""
    return None


@pytest.fixture(autouse=True)
def _sources_available() -> None:
    if not inventory.COMPOSE_FILES[0].exists():
        pytest.skip("run this on the host — the sources are not mounted in-network")


def _compose_text() -> dict[str, str]:
    # Keyed on the path relative to backend/, NOT on `path.name` — six of the seven files are
    # called `docker-compose.unit.yml`, so a name-keyed dict silently collapses to two entries
    # and five services never get examined. Mutation testing caught exactly that: re-introducing
    # the missing StreamingService timeout changed nothing, because its file had been overwritten
    # in the dict by whichever service came last.
    return {
        str(path.relative_to(inventory.BACKEND)): path.read_text(encoding="utf-8")
        for path in inventory.COMPOSE_FILES
    }


def _environment_keys(text: str) -> set[str]:
    """Every `Section__Key` an environment block sets, regardless of its value."""
    return set(re.findall(r"^\s+-\s+([A-Za-z][\w.]*__[\w:]+)=", text, re.MULTILINE))


# --- timeouts: every outbound call is bounded, and says so where it is configured -----


def test_every_configured_base_url_declares_its_own_timeout():
    """A `BaseUrl` with no `TimeoutSeconds` beside it is not untimed — it is timed by a C#
    default nobody reading the deployment can see.

    That is the failure mode, not an unbounded hang: the value exists, it is simply somewhere
    the person changing the environment will not look. Two of those were found and fixed in
    §14.3; StreamingService still had one when this rule was written.
    """
    missing: list[str] = []
    for name, text in _compose_text().items():
        keys = _environment_keys(text)
        for key in sorted(keys):
            section, _, leaf = key.partition("__")
            if leaf != "BaseUrl":
                continue
            if f"{section}__TimeoutSeconds" not in keys:
                missing.append(f"{name}: {section}")

    assert not missing, (
        "These clients have a configured base URL and no configured timeout, so the timeout lives "
        f"in code where a deployment cannot see or change it: {', '.join(missing)}"
    )


def test_object_storage_calls_are_bounded_and_do_not_retry_forever():
    # The AWS SDK's defaults are a 100s timeout and four retries with backoff. Correct for S3
    # across the internet; for a MinIO container one hop away it means a dependency that is
    # merely *down* holds a user's request for minutes rather than failing in one.
    classroom = inventory.BACKEND / "ClassroomService/docker-compose.unit.yml"
    content = classroom.read_text(encoding="utf-8")

    assert "S3Settings__TimeoutSeconds" in content, "no bound on how long an S3 call may take"
    assert "S3Settings__MaxErrorRetry" in content, "no bound on how many times it retries"


def test_the_storage_health_probe_is_tighter_than_a_storage_call():
    # /health is what the smoke suite and the readiness gate poll. A probe that waits as long as
    # the operation it is probing turns one sick service into a stalled orchestrator.
    content = (inventory.BACKEND / "ClassroomService/docker-compose.unit.yml").read_text(
        encoding="utf-8"
    )
    probe = int(re.search(r"S3Settings__HealthProbeTimeoutSeconds=(\d+)", content).group(1))
    call = int(re.search(r"S3Settings__TimeoutSeconds=(\d+)", content).group(1))

    assert probe < call, f"the health probe ({probe}s) may wait as long as a real call ({call}s)"


# --- credentials: one source, and no copies ------------------------------------------


def test_everything_that_talks_to_minio_reads_the_same_two_variables():
    """The rule that would have caught the defect this file was written after.

    Not "credentials are configured" — they were, for StreamingService's egress, which is why the
    platform looked fine. The invariant is that every consumer reads the *same* variables, so that
    rotating the password either works everywhere or fails everywhere, and never works in one
    service while silently breaking another.
    """
    consumers: dict[str, tuple[str, str]] = {
        "ClassroomService": ("S3Settings__AccessKey", "S3Settings__SecretKey"),
        "StreamingService": ("Egress__S3__AccessKey", "Egress__S3__Secret"),
    }

    wrong: list[str] = []
    for service, (access_key, secret_key) in consumers.items():
        content = (inventory.BACKEND / service / "docker-compose.unit.yml").read_text(
            encoding="utf-8"
        )
        for key, expected in ((access_key, MINIO_USER_VAR), (secret_key, MINIO_PASSWORD_VAR)):
            match = re.search(rf"{re.escape(key)}=(.+)", content)
            if match is None:
                wrong.append(f"{service}: {key} is not configured at all")
            elif expected not in match.group(1):
                wrong.append(f"{service}: {key} does not read ${{{expected}}}")

    assert not wrong, (
        "MinIO credentials must come from one place, or rotating the password fixes one service "
        f"and breaks another: {'; '.join(wrong)}"
    )


@pytest.mark.parametrize("credential", DEV_MINIO_CREDENTIALS)
def test_the_development_minio_credentials_appear_in_no_shipped_file(credential: str):
    """They were in a composition root and in two appsettings files, as *defaults*.

    A default credential that happens to match the development environment is the worst kind:
    it works, so nothing complains, and it goes on working until the day the real password
    differs — at which point the service falls back to a value that is wrong everywhere.
    """
    searched = [
        inventory.BACKEND / "ClassroomService/src/ClassroomService.Infrastructure/DependencyInjection.cs",
        inventory.BACKEND / "ClassroomService/src/ClassroomService.Infrastructure/Configuration/S3ClientFactory.cs",
        inventory.BACKEND / "StreamingService/src/StreamingService.Api/appsettings.json",
        inventory.BACKEND / "StreamingService/src/StreamingService.Api/appsettings.Development.json",
        *inventory.COMPOSE_FILES,
    ]

    offenders = [
        path.name
        for path in searched
        if path.exists() and credential in path.read_text(encoding="utf-8")
    ]

    assert not offenders, (
        f"the development MinIO credential {credential!r} is hard-coded in: {', '.join(offenders)}"
    )


def test_every_compose_file_actually_reaches_the_rules_above():
    """Guarding the guard, after it turned out not to be one.

    `_compose_text()` was keyed on the file *name*, and six of the seven compose files share the
    name `docker-compose.unit.yml` — so the dict held two entries and five services were never
    read. Every rule above passed while examining a fraction of the platform, which is the
    failure a conformance test cannot report about itself.
    """
    documents = _compose_text()

    assert len(documents) == len(inventory.COMPOSE_FILES), (
        "some compose files collapsed into one dictionary key, so they are not being checked: "
        f"{len(documents)} of {len(inventory.COMPOSE_FILES)}"
    )
    # And each one must actually contain settings, or "no missing timeouts" means "no timeouts".
    # Four, not seven: the root compose file starts infrastructure only, and the two Python
    # services use flat SCREAMING_SNAKE variables rather than `Section__Key` — those are checked
    # by `test_settings_binding.py`, which is the rule shaped for that convention.
    with_settings = [name for name, text in documents.items() if _environment_keys(text)]
    assert len(with_settings) == 4, (
        f"expected the four .NET services to carry Section__Key settings, found "
        f"{len(with_settings)}: {sorted(with_settings)}"
    )
