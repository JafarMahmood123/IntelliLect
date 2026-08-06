"""The results collector, and the claims `docs/testing-results.md` makes (§10.5). Nothing runs.

A results document is the one artifact whose errors nobody catches, because by the time it is
read the code has moved on and there is nothing left to check it against. So the collector is
tested for the two ways it could put a wrong number in front of a reader — and the harder of the
two is not a parsing bug, it is a **stale artifact**, which reads exactly like a fresh one.
"""

from __future__ import annotations

import re
from datetime import UTC, datetime, timedelta
from pathlib import Path

import pytest

from support import results
from support.inventory import BACKEND

pytestmark = [pytest.mark.smoke, pytest.mark.offline]


@pytest.fixture(scope="session", autouse=True)
def platform_ready() -> None:
    """Shadows conftest's readiness poll — these rules read files, not sockets."""
    return None


def _coverage(**overrides) -> results.Coverage:
    base = dict(
        component="Svc",
        how="dotnet test",
        line_rate=0.62,
        branch_rate=0.5,
        lines_valid=1000,
        measured_at=datetime(2026, 8, 6, tzinfo=UTC),
        newest_source_at=datetime(2026, 8, 5, tzinfo=UTC),
    )
    return results.Coverage(**{**base, **overrides})


# --- a number is only reported when it can be stood behind ---------------------------


def test_a_current_measurement_is_reported_with_its_date():
    row = _coverage().row()

    assert "62.0%" in row
    assert "2026-08-06" in row


def test_a_measurement_older_than_the_code_withholds_the_number_entirely():
    """The failure this collector exists to prevent.

    A coverage artifact is a file. It reads identically whether it was written a minute ago or
    a month ago against code that has since changed — and when it was written first, this
    project's own artifacts were three commits out of date and would have been quoted as
    current. Rounding it down or flagging it in a footnote is not enough: a stale percentage is
    not an approximate claim, it is a precise claim about code that no longer exists.
    """
    stale = _coverage(
        measured_at=datetime(2026, 8, 1, tzinfo=UTC),
        newest_source_at=datetime(2026, 8, 6, tzinfo=UTC),
    )

    assert stale.stale
    assert stale.status == "STALE"
    row = stale.row()
    assert "62.0%" not in row, "a stale percentage must not be printed at all"
    assert "stale" in row.lower()
    assert "2026-08-01" in row and "2026-08-06" in row  # both dates, so the gap is visible


def test_a_missing_artifact_says_so_and_says_how_to_produce_it():
    row = _coverage(line_rate=None, branch_rate=None, measured_at=None).row()

    assert "not measured" in row
    assert "dotnet test" in row, "a gap the reader cannot close is only half a report"


def test_an_artifact_measured_after_the_last_edit_is_current():
    # The boundary matters: equal timestamps are current, not stale. A run that finishes in the
    # same second as the last save would otherwise be discarded for no reason.
    same = datetime(2026, 8, 6, tzinfo=UTC)

    assert not _coverage(measured_at=same, newest_source_at=same).stale
    assert not _coverage(
        measured_at=same, newest_source_at=same - timedelta(seconds=1)
    ).stale


def test_staleness_is_undecidable_rather_than_assumed_when_a_date_is_missing():
    # No source timestamp means the comparison cannot be made. Guessing "stale" would suppress a
    # real number; guessing "current" is what the code does, and the artifact date is printed
    # beside it so the reader can judge.
    assert not _coverage(newest_source_at=None).stale


# --- parsing ------------------------------------------------------------------------


def test_lcov_totals_are_summed_rather_than_averaged(tmp_path: Path, monkeypatch):
    """Percentages per file cannot be averaged into a project percentage — a 100%-covered
    three-line file would count as much as a 10%-covered thousand-line one. LCOV's LF/LH are
    raw counts, which is why they are what gets read."""
    coverage_dir = tmp_path / "front-end-web/coverage"
    coverage_dir.mkdir(parents=True)
    (coverage_dir / "lcov.info").write_text(
        "SF:a.ts\nLF:1000\nLH:100\nBRF:10\nBRH:5\nend_of_record\n"
        "SF:b.ts\nLF:3\nLH:3\nBRF:0\nBRH:0\nend_of_record\n",
        encoding="utf-8",
    )
    (tmp_path / "front-end-web/src").mkdir(parents=True)
    monkeypatch.setattr(results, "REPO", tmp_path)

    measured = results.frontend_coverage()

    assert measured.lines_valid == 1003
    assert measured.line_rate == pytest.approx(103 / 1003)  # not (10% + 100%) / 2


def test_the_real_artifacts_parse_or_report_themselves_missing():
    # Whatever is on disk right now, every component must produce a row — a component the
    # collector silently drops is one the report will silently omit.
    rows = results.all_coverage()

    assert len(rows) == len(results.DOTNET_SERVICES) + len(results.PYTHON_SERVICES) + 1
    assert all(row.row().startswith("| ") for row in rows)


@pytest.mark.parametrize("service", [*results.DOTNET_SERVICES, *results.PYTHON_SERVICES])
def test_a_component_with_no_artifact_at_all_still_says_how_to_produce_one(
    service: str, tmp_path: Path, monkeypatch
):
    """Exercises the branch the parametrised test above cannot reach right now.

    Every artifact happens to exist on this machine, so `artifact is None` is never taken and
    blanking `how` there survived mutation. Pointing the collector at an empty tree is the only
    way to reach it — and that branch is precisely the one a reader hits when a suite has never
    been run.
    """
    monkeypatch.setattr(results, "BACKEND", tmp_path)

    for measured in (results.dotnet_coverage(service), results.python_coverage(service)):
        assert measured.line_rate is None
        assert "not measured" in measured.row()
        assert measured.how.strip(), "a component with no artifact still needs its command"
        assert service in measured.how


@pytest.mark.parametrize("component", [c.component for c in results.all_coverage()])
def test_every_component_carries_a_command_that_would_produce_its_number(component: str):
    """Not decoration — it is the difference between a gap and a dead end.

    "not measured" is an honest row only if the reader can act on it. Mutation testing found
    this: blanking `how` for a missing artifact changed nothing, because the missing-artifact
    test built its own `Coverage` with `how` already filled in and never exercised the real
    collector's version.
    """
    row = next(c for c in results.all_coverage() if c.component == component)

    assert row.how.strip(), f"{component} has no command to reproduce its number"
    assert any(tool in row.how for tool in ("dotnet test", "pytest", "npm")), row.how


# --- the document the collector writes into ------------------------------------------


def test_the_generated_blocks_round_trip_without_touching_the_prose():
    document = (
        "# Results\n\nprose that must survive\n\n"
        "<!-- generated:coverage -->\nold table\n<!-- /generated:coverage -->\n\n"
        "more prose\n"
        "<!-- generated:layers -->\nold\n<!-- /generated:layers -->\n"
        "<!-- generated:latency -->\nold\n<!-- /generated:latency -->\n"
        "<!-- generated:stamp -->\nold\n<!-- /generated:stamp -->\n"
    )

    import collect_results

    filled = collect_results.fill(document)

    assert "prose that must survive" in filled
    assert "more prose" in filled
    assert "old table" not in filled
    assert "| Component | Line |" in filled


def test_a_missing_marker_stops_rather_than_silently_writing_nothing():
    import collect_results

    with pytest.raises(SystemExit, match="generated:coverage"):
        collect_results.fill("# Results\n\nno markers here\n")


def test_the_results_document_still_has_every_block_the_collector_fills():
    import collect_results

    document = collect_results.TARGET.read_text(encoding="utf-8")

    for name in collect_results.BLOCKS:
        assert f"<!-- generated:{name} -->" in document, f"lost the {name} block"


# --- the claim the results document makes about this suite ---------------------------


# Modules that replace conftest's readiness gate for a reason OTHER than needing nothing.
# Being here is a decision, which is the point of it being a list.
SHADOWS_BUT_NEEDS_A_PLATFORM: dict[str, str] = {
    "test_smoke.py": (
        "replaces the two-minute poll with a zero-length one on purpose — a smoke run answers "
        "'is this alive NOW', and waiting turns a dead service into a slow pass"
    ),
}


def test_every_module_that_needs_nothing_running_is_marked_offline():
    """`docs/testing-results.md` publishes a count of tests that run with no platform.

    The topical markers cannot carry that claim: `-m smoke` and `-m latency` each select both a
    containerless rule module and a module that needs the real thing. Running the topical
    selection with the platform down hangs for two minutes and then fails eight tests — which is
    how this rule came to exist.

    Shadowing conftest's `platform_ready` is the signal, because a module that talks to nothing
    has to shadow it or pay for the poll. It is not a perfect signal — `test_smoke.py` shadows it
    to make the wait *shorter*, which the rule flagged the first time it ran — so the exception is
    named and reasoned rather than the rule being weakened to accommodate it.
    """
    suite = BACKEND / "tests/e2e"
    shadows: set[str] = set()
    marked: set[str] = set()

    for module in sorted(suite.glob("test_*.py")):
        source = module.read_text(encoding="utf-8")
        if re.search(r"^def platform_ready\(", source, re.MULTILINE):
            shadows.add(module.name)
        if "pytest.mark.offline" in source:
            marked.add(module.name)

    assert shadows, "found no modules shadowing platform_ready — has the fixture been renamed?"

    unmarked = sorted(shadows - marked - set(SHADOWS_BUT_NEEDS_A_PLATFORM))
    assert not unmarked, (
        "these bypass conftest's readiness gate but are not marked `offline`, so they are missing "
        f"from the count the results document publishes: {unmarked}"
    )

    # The other direction: `offline` without the shadow means conftest's two-minute poll runs
    # anyway, so the module is not offline at all — it just says it is.
    unshadowed = sorted(marked - shadows)
    assert not unshadowed, (
        f"marked `offline` but still waiting on conftest's platform gate: {unshadowed}"
    )


def test_no_module_is_excused_that_no_longer_exists_or_no_longer_needs_to_be():
    # Both directions on the exception list, as everywhere else: an entry for a deleted module
    # silently excuses the next module to take the name, and an entry for one that has since
    # become offline makes the list stop meaning what it says.
    suite = BACKEND / "tests/e2e"
    present = {path.name for path in suite.glob("test_*.py")}

    stale = sorted(set(SHADOWS_BUT_NEEDS_A_PLATFORM) - present)
    assert not stale, f"excused but no such module: {stale}"

    unnecessary = sorted(
        name
        for name in SHADOWS_BUT_NEEDS_A_PLATFORM
        if "pytest.mark.offline" in (suite / name).read_text(encoding="utf-8")
    )
    assert not unnecessary, f"excused but now marked offline — remove from the list: {unnecessary}"

    unexplained = sorted(n for n, why in SHADOWS_BUT_NEEDS_A_PLATFORM.items() if not why.strip())
    assert not unexplained, f"excused with no reason: {unexplained}"
