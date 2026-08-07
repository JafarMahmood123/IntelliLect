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
        "<!-- generated:inventory -->\nold counts\n<!-- /generated:inventory -->\n\n"
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
    assert "old counts" not in filled
    assert "| Component | Line |" in filled
    assert "| Suite | Tests |" in filled


def test_a_missing_marker_stops_rather_than_silently_writing_nothing():
    import collect_results

    # Matches the marker syntax rather than one block's name: which block is missed first
    # depends on BLOCKS' insertion order, and this rule is about stopping, not about order.
    with pytest.raises(SystemExit, match="has no <!-- generated:"):
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


# --- the inventory table, which was the last number typed by hand --------------------


def _suite(**overrides) -> results.Suite:
    base = dict(
        name="Svc",
        how="dotnet test",
        passed=100,
        skipped=0,
        failed=0,
        ran_at=datetime(2026, 8, 6, tzinfo=UTC),
        newest_source_at=datetime(2026, 8, 5, tzinfo=UTC),
    )
    return results.Suite(**{**base, **overrides})


def test_a_current_count_is_reported_with_its_date():
    row = _suite().row()

    assert "100" in row
    assert "2026-08-06" in row
    assert "stale" not in row


def test_a_count_older_than_the_tests_it_counts_is_withheld():
    # The sharper half of R-01. A coverage artifact goes stale when production code changes; a
    # COUNT goes stale the moment somebody adds a test, which is the most common edit anyone
    # makes here. Both dates are shown so the reader can see how far out it is.
    row = _suite(newest_source_at=datetime(2026, 8, 7, tzinfo=UTC)).row()

    assert "100" not in row
    assert "stale" in row
    assert "2026-08-06" in row and "2026-08-07" in row


def test_a_count_taken_at_the_same_moment_as_the_change_is_current():
    # R-04's analogue, and it was missing until a mutation asked for it. Both stamps land in the
    # same second often enough — a run that finishes as the file is saved — and treating equal as
    # stale would withhold a number that is perfectly good.
    row = _suite(
        ran_at=datetime(2026, 8, 6, tzinfo=UTC),
        newest_source_at=datetime(2026, 8, 6, tzinfo=UTC),
    ).row()

    assert "100" in row
    assert "stale" not in row


def test_a_suite_with_failures_is_reported_as_failing_and_not_as_a_count():
    # Louder than staleness, and the reason `failed` is read at all. A suite with four failures
    # still has a passed count, and printing it in a column headed "Tests" states that the suite
    # passes. Nothing else in this document would contradict it.
    row = _suite(passed=96, failed=4).row()

    assert "96" not in row
    assert "FAILING" in row


def test_a_suite_that_was_never_run_says_which_command_would_run_it():
    row = _suite(passed=None).row()

    assert "not run" in row
    assert "dotnet test" in row


def test_skips_are_shown_rather_than_folded_into_the_count():
    # 384 and "384 (+13 skipped)" are different claims, and the second is the true one.
    row = _suite(passed=384, skipped=13).row()

    assert "384" in row
    assert "13 skipped" in row


# --- the total, which is where an incomplete table does its damage -------------------


def test_the_total_is_the_sum_when_every_row_can_be_quoted():
    table = results.inventory_table([_suite(name="A", passed=10), _suite(name="B", passed=32)])

    assert "**42**" in table


def test_the_total_is_withheld_when_any_row_is_and_says_which():
    # The rule worth having. Summing the readable rows produces a smaller number that looks
    # exactly like a real one — and a report that under-counts silently has precisely the defect
    # of one that over-counts. It must refuse, and name what is missing.
    table = results.inventory_table(
        [_suite(name="A", passed=10), _suite(name="Stale", newest_source_at=datetime(2026, 8, 9, tzinfo=UTC))]
    )

    assert "**42**" not in table
    assert "incomplete" in table
    assert "Stale" in table


def test_a_failing_suite_also_stops_the_total():
    table = results.inventory_table([_suite(name="A", passed=10), _suite(name="Broken", failed=1)])

    assert "incomplete" in table
    assert "Broken" in table


def test_the_totals_skips_are_carried_up_rather_than_dropped():
    table = results.inventory_table([_suite(passed=10, skipped=2), _suite(name="B", passed=32, skipped=1)])

    assert "**42**" in table
    assert "3 skipped" in table


# --- the readers, against the shapes the runners actually write ----------------------


def _trx(tmp_path: Path, reversed_order: bool = False, **counters: int) -> Path:
    """The real shape `dotnet test --logger trx` emits, including the counters we ignore."""
    attributes = {
        "total": 61, "executed": 59, "passed": 59, "failed": 0, "error": 0,
        "timeout": 0, "aborted": 0, "inconclusive": 0, "passedButRunAborted": 0,
        "notRunnable": 0, "notExecuted": 2, "disconnected": 0, "warning": 0,
        "completed": 0, "inProgress": 0, "pending": 0,
        **counters,
    }
    if reversed_order:
        attributes = dict(reversed(list(attributes.items())))
    rendered = " ".join(f'{key}="{value}"' for key, value in attributes.items())
    artifact = tmp_path / "r.trx"
    artifact.write_text(
        '<?xml version="1.0"?><TestRun>'
        '<Times creation="2026-08-06T10:00:00.0+03:00" start="2026-08-06T10:00:00.0+03:00" '
        'finish="2026-08-06T10:00:09.0+03:00" />'
        f"<ResultSummary><Counters {rendered} /></ResultSummary></TestRun>",
        encoding="utf-8",
    )
    return artifact


def test_the_trx_reader_matches_what_dotnet_writes(tmp_path: Path):
    suite = results._trx_suite(
        "Svc", "dotnet test", _trx(tmp_path), datetime(2026, 8, 5, tzinfo=UTC)
    )

    assert suite.passed == 59
    # A TRX has no "skipped" attribute — what did not run is total minus executed. Reading it as
    # zero loses two tests from the count and from the total, silently.
    assert suite.skipped == 2
    assert suite.failed == 0
    # The run's own finish stamp, not the file's mtime: a copied file keeps its mtime.
    assert suite.ran_at.date().isoformat() == "2026-08-06"


def test_the_trx_reader_counts_errors_as_failures(tmp_path: Path):
    # `error` and `failed` are separate counters and both mean the suite is not passing. A reader
    # that watched only `failed` would print a clean count for a run that crashed.
    suite = results._trx_suite(
        "Svc", "dotnet test", _trx(tmp_path, error=3), datetime(2026, 8, 5, tzinfo=UTC)
    )

    assert suite.failed == 3
    assert not suite.quotable
    assert "FAILING" in suite.row()


def test_the_trx_reader_does_not_depend_on_the_order_of_the_attributes(tmp_path: Path):
    # XML attribute order is not semantically significant, so a reader that works only for
    # dotnet's current emission order is relying on something the format does not promise.
    #
    # Worth recording what this does NOT prove, because the first version of the comment here
    # claimed it. The `\b` anchor in the reader guards against `executed="` matching inside
    # `notExecuted="`, and it cannot — the counter is spelled with a capital E, so the two never
    # collide whatever the order. A mutation removing the anchor survives, honestly: it is
    # defence against a name collision that TRX does not contain.
    suite = results._trx_suite(
        "Svc",
        "dotnet test",
        _trx(tmp_path, reversed_order=True, total=61, executed=59, passed=59, notExecuted=2),
        datetime(2026, 8, 5, tzinfo=UTC),
    )

    assert suite.passed == 59
    assert suite.skipped == 2


def test_the_junit_reader_totals_every_suite_element(tmp_path: Path):
    # vitest writes one <testsuite> per FILE and pytest writes one for the whole run, so a reader
    # that took the first element would report one file's worth of a 40-file frontend suite.
    artifact = tmp_path / "test-results.xml"
    artifact.write_text(
        '<testsuites name="vitest tests">'
        '<testsuite name="a.test.ts" tests="15" failures="0" errors="0" skipped="0" '
        'timestamp="2026-08-06T10:00:00.000Z" />'
        '<testsuite name="b.test.ts" tests="27" failures="0" errors="1" skipped="2" '
        'timestamp="2026-08-06T10:00:01.000Z" />'
        "</testsuites>",
        encoding="utf-8",
    )

    suite = results._junit_suite("fe", "npx vitest run", artifact, datetime(2026, 8, 5, tzinfo=UTC))

    assert suite.passed == 15 + 27 - 2 - 1
    assert suite.skipped == 2
    assert suite.failed == 1


def test_a_missing_artifact_is_not_read_as_a_zero(tmp_path: Path):
    # The failure that would be invisible: a suite reported as "0 tests, passing".
    suite = results._junit_suite("gone", "cmd", tmp_path / "absent.xml", None)

    assert suite.passed is None
    assert not suite.quotable
    assert "not run" in suite.row()


# --- both directions on the suite list ----------------------------------------------


def test_every_suite_the_document_reports_has_a_reader():
    # A suite dropped from this list is a suite the report silently omits — the R-07 rule, for
    # the other table. Named explicitly so adding a service means touching this test.
    names = {suite.name for suite in results.all_suites()}

    for expected in (*results.DOTNET_SERVICES, *results.PYTHON_SERVICES, "front-end-web"):
        assert expected in names
    assert any(name.startswith("Cross-service E2E") for name in names)


def test_the_cross_service_row_counts_only_what_can_run_here():
    # It reports the `-m offline` subset deliberately. The rest is authored and has never
    # executed, and a table that added it in would be asserting that those tests pass.
    suite = results.e2e_suite()

    assert "offline" in suite.how
    assert "offline" in suite.name.lower()


def test_the_commands_in_the_table_are_the_ones_that_write_the_artifacts():
    # A command that does not produce the artifact leaves a reader running it, seeing green, and
    # finding the table unchanged — with nothing to explain why.
    for suite in results.all_suites():
        assert any(flag in suite.how for flag in ("--logger trx", "--junitxml", "--reporter=junit")), suite.name
