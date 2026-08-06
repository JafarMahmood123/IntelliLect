"""Every variable the deployment sets binds to something that reads it (§10.4). Nothing runs.

Both Python services declare `extra="ignore"` on their pydantic-settings class, so a compose
file can set a variable that binds to no field and **nothing anywhere says so**. That is a
convenience with a real cost: it lets these services share one `.env` with their siblings, and
it also lets a renamed setting go quietly dead.

Which is what happened. The §1 rename moved `KNOWLEDGE_BASE_URL` to `RAG_BASE_URL` in the
compose file, the README, the tests and the service's own error messages — but not in the
`Settings` field it binds to, which stayed `knowledge_base_url`. So LiveAssistantService set
`RAG_BASE_URL=http://rag-service:8080`, bound nothing, and ran retrieval against an empty base
URL. Every idea failed retrieval, and `IdeaEvaluator` degrades a failed retrieval to "no
feedback" — which looks exactly like having nothing useful to say.

Nothing caught it because every test constructs `Settings(...)` in Python and passes the value
directly, which is the one path that cannot exercise the env binding.

The rule is deliberately static — it reads the source rather than importing it, because each
service's `Settings` needs that service's own virtualenv and this suite has neither.
"""

from __future__ import annotations

import re

import pytest

from support import inventory

# `offline` is the marker that means what it says: this module needs nothing running.
# The topical marker alone does not — `-m smoke` and `-m latency` both also select
# modules that require a live platform.
pytestmark = [pytest.mark.resilience, pytest.mark.offline]

PYTHON_SERVICES = {
    "RagService": (
        inventory.BACKEND / "RagService/docker-compose.unit.yml",
        inventory.BACKEND / "RagService/app/infrastructure/config/settings.py",
    ),
    "LiveAssistantService": (
        inventory.BACKEND / "LiveAssistantService/docker-compose.unit.yml",
        inventory.BACKEND / "LiveAssistantService/app/infrastructure/config/settings.py",
    ),
}

# Variables consumed by a library rather than by Settings, each with what reads it. Being on
# this list is a decision; an unexplained entry is indistinguishable from a silenced failure.
CONSUMED_ELSEWHERE: dict[str, str] = {
    "HF_HOME": "huggingface_hub reads it directly — model cache location",
    "HF_HUB_DISABLE_XET": "huggingface_hub reads it directly — transfer backend switch",
}


@pytest.fixture(scope="session", autouse=True)
def platform_ready() -> None:
    """Shadows conftest's readiness poll — these rules read files, not sockets."""
    return None


@pytest.fixture(autouse=True)
def _sources_available() -> None:
    if not inventory.COMPOSE_FILES[0].exists():
        pytest.skip("run this on the host — the sources are not mounted in-network")


def _environment_variables(compose_text: str) -> set[str]:
    """Every SCREAMING_SNAKE variable an environment block sets."""
    return set(re.findall(r"^\s+-\s+([A-Z][A-Z0-9_]*)=", compose_text, re.MULTILINE))


def _bindable_names(settings_source: str) -> set[str]:
    """Every name a `Settings` field can be populated from: its own name, plus any alias.

    Read statically. Importing the class would need the owning service's virtualenv, and this
    suite has its own — a rule that could only run inside one service is a rule that will not
    be run.
    """
    fields = {
        name.upper()
        for name in re.findall(r"^    ([a-z][a-z0-9_]*)\s*:", settings_source, re.MULTILINE)
    }
    aliases: set[str] = set()
    for group in re.findall(r"AliasChoices\(([^)]*)\)", settings_source):
        aliases |= {
            part.strip().strip("\"'").upper() for part in group.split(",") if part.strip()
        }
    return fields | aliases


@pytest.mark.parametrize("service", sorted(PYTHON_SERVICES))
def test_no_configured_variable_binds_to_nothing(service: str):
    compose_path, settings_path = PYTHON_SERVICES[service]
    configured = _environment_variables(compose_path.read_text(encoding="utf-8"))
    bindable = _bindable_names(settings_path.read_text(encoding="utf-8"))

    orphans = sorted(configured - bindable - set(CONSUMED_ELSEWHERE))

    assert not orphans, (
        f"{service}'s compose file sets these and Settings declares no field or alias for them, "
        f"so `extra=\"ignore\"` discards them silently: {', '.join(orphans)}"
    )


@pytest.mark.parametrize("service", sorted(PYTHON_SERVICES))
def test_the_rule_is_reading_a_real_pair_of_files(service: str):
    # A regex that stopped matching would make the rule above pass over an empty set, which is
    # the failure a conformance test cannot report about itself.
    compose_path, settings_path = PYTHON_SERVICES[service]

    assert len(_environment_variables(compose_path.read_text(encoding="utf-8"))) >= 10
    assert len(_bindable_names(settings_path.read_text(encoding="utf-8"))) >= 30


def test_every_library_exemption_names_what_reads_it():
    unexplained = sorted(name for name, why in CONSUMED_ELSEWHERE.items() if not why.strip())

    assert not unexplained, f"exempt with no reason: {', '.join(unexplained)}"


def test_the_retrieval_url_binds_to_the_name_the_deployment_actually_sets():
    """The specific regression, pinned by name.

    The general rule above would catch it again, but this one states the fact plainly: compose,
    the README, the tests and the error messages all say `RAG_BASE_URL`, so `RAG_BASE_URL` is
    what must bind. The old name stays accepted so an existing `.env` keeps working.
    """
    source = PYTHON_SERVICES["LiveAssistantService"][1].read_text(encoding="utf-8")
    bindable = _bindable_names(source)

    assert "RAG_BASE_URL" in bindable
    assert "KNOWLEDGE_BASE_URL" in bindable, "the pre-rename name should still be accepted"
    assert "settings.rag_base_url" not in source  # the field, not a self-reference
