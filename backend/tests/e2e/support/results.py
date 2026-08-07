"""Collecting the numbers the report states as fact (work-plan §10.5).

The template this fills is `docs/testing-results.md`. The reason it is filled by a script
rather than by hand is not tidiness — it is that **the failure mode of a results section is a
number that was true once**.

Two ways that happens, and both are handled here rather than trusted to whoever writes the
chapter up:

**Transcription.** Six services, each with a coverage percentage, copied into a table by hand at
some point during a week of changes. One of them will be wrong and nothing will ever say which.

**Staleness.** A coverage artifact is a file. It sits there after the run that made it, and it
reads exactly the same whether it was written a minute ago or a month ago against code that has
since changed. So every artifact here is compared against the source it claims to measure, and
one that is older than the code is reported as **stale** — never as a number. A missing artifact
is honest; a stale one is worse than nothing, because it is specific.
"""

from __future__ import annotations

import re
import xml.etree.ElementTree as ElementTree
from dataclasses import dataclass
from datetime import UTC, datetime
from pathlib import Path

from support.inventory import BACKEND

REPO = BACKEND.parent
LATENCY_RESULTS = BACKEND / "tests/e2e/latency-results.md"


@dataclass(frozen=True)
class Coverage:
    """One service's coverage, and whether it can be believed."""

    component: str
    how: str  # the command that produces the artifact
    line_rate: float | None = None
    branch_rate: float | None = None
    lines_valid: int | None = None
    artifact: Path | None = None
    measured_at: datetime | None = None
    newest_source_at: datetime | None = None

    @property
    def stale(self) -> bool:
        """Measured before the code it measures was last changed."""
        if self.measured_at is None or self.newest_source_at is None:
            return False
        return self.measured_at < self.newest_source_at

    @property
    def status(self) -> str:
        if self.line_rate is None:
            return "not measured"
        return "STALE" if self.stale else "current"

    def row(self) -> str:
        if self.line_rate is None:
            return f"| {self.component} | — | — | not measured | `{self.how}` |"
        if self.stale:
            # Deliberately withholds the number. A stale percentage in a report is not a rough
            # figure, it is a precise claim about code that no longer exists.
            return (
                f"| {self.component} | — | — | **stale — re-run before quoting** "
                f"(measured {self.measured_at:%Y-%m-%d}, code changed "
                f"{self.newest_source_at:%Y-%m-%d}) | `{self.how}` |"
            )
        branch = f"{self.branch_rate * 100:.1f}%" if self.branch_rate is not None else "—"
        return (
            f"| {self.component} | {self.line_rate * 100:.1f}% | {branch} | "
            f"measured {self.measured_at:%Y-%m-%d} ({self.lines_valid} lines) | `{self.how}` |"
        )


def _newest_mtime(root: Path, patterns: tuple[str, ...]) -> datetime | None:
    times = [
        path.stat().st_mtime
        for pattern in patterns
        for path in root.rglob(pattern)
        if "/obj/" not in str(path) and "/bin/" not in str(path) and "/node_modules/" not in str(path)
    ]
    return datetime.fromtimestamp(max(times), UTC) if times else None


def _newest(paths: list[Path]) -> Path | None:
    return max(paths, key=lambda p: p.stat().st_mtime) if paths else None


def dotnet_coverage(service: str) -> Coverage:
    """Newest Cobertura report under the service's test project."""
    how = f"cd backend/{service} && dotnet test --collect:'XPlat Code Coverage' -s ../coverlet.runsettings"
    tests_root = BACKEND / service / "tests"
    artifact = _newest(list(tests_root.rglob("coverage.cobertura.xml"))) if tests_root.exists() else None
    source_at = _newest_mtime(BACKEND / service / "src", ("*.cs",))

    if artifact is None:
        return Coverage(component=service, how=how, newest_source_at=source_at)

    root = ElementTree.parse(artifact).getroot()
    return Coverage(
        component=service,
        how=how,
        line_rate=float(root.get("line-rate", 0)),
        branch_rate=float(root.get("branch-rate", 0)),
        lines_valid=int(root.get("lines-valid", 0)),
        artifact=artifact,
        # Cobertura's own timestamp, not the file's mtime: a copied or restored file keeps its
        # mtime but not its meaning.
        measured_at=datetime.fromtimestamp(int(root.get("timestamp", 0)), UTC),
        newest_source_at=source_at,
    )


def python_coverage(service: str) -> Coverage:
    """`coverage.xml` from `pytest --cov --cov-report=xml`, which is Cobertura too."""
    how = f"cd backend/{service} && .venv/bin/python -m pytest --cov=app --cov-report=xml"
    artifact = BACKEND / service / "coverage.xml"
    source_at = _newest_mtime(BACKEND / service / "app", ("*.py",))

    if not artifact.exists():
        return Coverage(component=service, how=how, newest_source_at=source_at)

    root = ElementTree.parse(artifact).getroot()
    return Coverage(
        component=service,
        how=how,
        line_rate=float(root.get("line-rate", 0)),
        branch_rate=float(root.get("branch-rate", 0)),
        lines_valid=int(root.get("lines-valid", 0)),
        artifact=artifact,
        measured_at=datetime.fromtimestamp(int(root.get("timestamp", 0)) / 1000, UTC),
        newest_source_at=source_at,
    )


def frontend_coverage() -> Coverage:
    """Totalled from `lcov.info`, which vitest already writes.

    Not `coverage-summary.json` — that would need a reporter added to `vitest.config.ts`, and a
    results collector should read what the project produces rather than change the project so it
    is easier to read. LCOV's LF/LH and BRF/BRH are exact totals, not rounded percentages.
    """
    how = "cd front-end-web && npm run test:coverage"
    artifact = REPO / "front-end-web/coverage/lcov.info"
    source_at = _newest_mtime(REPO / "front-end-web/src", ("*.ts", "*.tsx"))

    if not artifact.exists():
        return Coverage(component="front-end-web", how=how, newest_source_at=source_at)

    totals = {key: 0 for key in ("LF", "LH", "BRF", "BRH")}
    for line in artifact.read_text(encoding="utf-8").splitlines():
        key, _, value = line.partition(":")
        if key in totals and value.strip().isdigit():
            totals[key] += int(value)

    return Coverage(
        component="front-end-web",
        how=how,
        line_rate=totals["LH"] / totals["LF"] if totals["LF"] else 0.0,
        branch_rate=totals["BRH"] / totals["BRF"] if totals["BRF"] else None,
        lines_valid=totals["LF"],
        artifact=artifact,
        measured_at=datetime.fromtimestamp(artifact.stat().st_mtime, UTC),
        newest_source_at=source_at,
    )


DOTNET_SERVICES = ("UserManagementService", "ClassroomService", "StreamingService", "EmailService")
PYTHON_SERVICES = ("RagService", "LiveAssistantService")


def all_coverage() -> list[Coverage]:
    return [
        *(dotnet_coverage(service) for service in DOTNET_SERVICES),
        *(python_coverage(service) for service in PYTHON_SERVICES),
        frontend_coverage(),
    ]


def coverage_table(rows: list[Coverage]) -> str:
    header = (
        "| Component | Line | Branch | Status | How it is produced |\n"
        "|---|---|---|---|---|"
    )
    return "\n".join([header, *(row.row() for row in rows)])


def latency_table() -> str:
    """The §9 harness's own output, quoted rather than re-typed."""
    if not LATENCY_RESULTS.exists():
        return (
            "_Not yet run._ `cd backend && docker compose up -d && cd tests/e2e && "
            "./run-in-network.sh -m latency` writes `backend/tests/e2e/latency-results.md`; "
            "its table belongs here verbatim. Budgets and derivations: `docs/latency.md`."
        )
    text = LATENCY_RESULTS.read_text(encoding="utf-8")
    match = re.search(r"^\| Hop \|.*(?:\n\|.*)+", text, re.MULTILINE)
    return match.group(0) if match else text


# --- per-layer detail, because the headline hides the shape ------------------------


@dataclass(frozen=True)
class LayerCoverage:
    component: str
    layer: str
    line_rate: float


def dotnet_layers(service: str) -> list[LayerCoverage]:
    """Per-assembly coverage, which is the number worth reading.

    The headline is a ratio over whatever assemblies a test run happened to load, and that
    denominator moves for reasons unrelated to testing. UserManagementService is the live
    example: its Infrastructure assembly was loaded by no test at all, so it was absent from
    the report entirely and the headline was computed over Application + Domain. Adding one
    Infrastructure test pulled 300 lines of untested adapter code into the denominator and the
    headline FELL from 62.1% to 49.6% — while the tests went from 120 to 179 and Application
    rose. A report that quotes only the headline records that as a regression.
    """
    tests_root = BACKEND / service / "tests"
    artifact = _newest(list(tests_root.rglob("coverage.cobertura.xml"))) if tests_root.exists() else None
    if artifact is None:
        return []
    root = ElementTree.parse(artifact).getroot()
    return [
        LayerCoverage(
            component=service,
            layer=package.get("name", "").removeprefix(f"{service}."),
            line_rate=float(package.get("line-rate", 0)),
        )
        for package in root.iter("package")
    ]


def layer_table(services: tuple[str, ...] = DOTNET_SERVICES) -> str:
    order = {"Domain": 0, "Application": 1, "Infrastructure": 2, "Presentation": 3}
    rows = [
        layer
        for service in services
        for layer in sorted(dotnet_layers(service), key=lambda l: order.get(l.layer, 9))
    ]
    header = "| Service | Layer | Line |\n|---|---|---|"
    return "\n".join(
        [header, *(f"| {r.component} | {r.layer} | {r.line_rate * 100:.1f}% |" for r in rows)]
    )


# --- how many tests there are, which was the last number still typed by hand ----------


@dataclass(frozen=True)
class Suite:
    """One suite's result, and whether it can be believed.

    The same two failure modes the module docstring names, applied to the number this document
    was still transcribing. The staleness half is sharper here than it is for coverage: a count
    goes wrong the moment somebody adds a test, which is the single most common change anyone
    makes to this repository.
    """

    name: str
    how: str
    passed: int | None = None
    skipped: int = 0
    failed: int = 0
    artifact: Path | None = None
    ran_at: datetime | None = None
    newest_source_at: datetime | None = None
    note: str = ""

    @property
    def stale(self) -> bool:
        if self.ran_at is None or self.newest_source_at is None:
            return False
        return self.ran_at < self.newest_source_at

    @property
    def quotable(self) -> bool:
        """Whether this row's number may be added into the total."""
        return self.passed is not None and not self.stale and self.failed == 0

    def row(self) -> str:
        if self.passed is None:
            return f"| {self.name} | — | not run | `{self.how}` |"

        if self.failed:
            # Louder than staleness. A suite with failures has a count, and quoting it as though
            # it were a clean run is the one thing a results table must never do.
            return (
                f"| {self.name} | — | **{self.failed} FAILING** — fix before quoting "
                f"(ran {self.ran_at:%Y-%m-%d}) | `{self.how}` |"
            )

        if self.stale:
            return (
                f"| {self.name} | — | **stale — re-run before quoting** "
                f"(ran {self.ran_at:%Y-%m-%d}, tests or code changed "
                f"{self.newest_source_at:%Y-%m-%d}) | `{self.how}` |"
            )

        count = f"{self.passed}"
        if self.skipped:
            count += f" (+{self.skipped} skipped)"
        note = f" {self.note}" if self.note else ""
        return f"| {self.name} | {count} | passed {self.ran_at:%Y-%m-%d}{note} | `{self.how}` |"


def _junit_suite(
    name: str, how: str, artifact: Path, source_at: datetime | None, note: str = ""
) -> Suite:
    """Reads the JUnit XML that pytest and vitest both write."""
    if not artifact.exists():
        return Suite(name=name, how=how, newest_source_at=source_at, note=note)

    root = ElementTree.parse(artifact).getroot()
    suites = list(root.iter("testsuite"))
    total = sum(int(s.get("tests", 0)) for s in suites)
    skipped = sum(int(s.get("skipped", 0)) for s in suites)
    failed = sum(int(s.get("failures", 0)) + int(s.get("errors", 0)) for s in suites)

    # pytest stamps the suite; vitest stamps each file. Either way the RUN's own time is used
    # rather than the file's mtime, for the reason dotnet_coverage gives.
    stamps = [s.get("timestamp") for s in suites if s.get("timestamp")]
    ran_at = (
        max(datetime.fromisoformat(stamp) for stamp in stamps).astimezone(UTC)
        if stamps
        else datetime.fromtimestamp(artifact.stat().st_mtime, UTC)
    )

    return Suite(
        name=name,
        how=how,
        passed=total - skipped - failed,
        skipped=skipped,
        failed=failed,
        artifact=artifact,
        ran_at=ran_at,
        newest_source_at=source_at,
        note=note,
    )


def _trx_suite(name: str, how: str, artifact: Path, source_at: datetime | None) -> Suite:
    """Reads the TRX that `dotnet test --logger trx` writes.

    Takes the path rather than finding it, so a test can drive it with a known file. The first
    version did the lookup itself and its "test" asserted `61 - 59 == 2` against a literal — a
    tautology that exercised no code at all, which a surviving mutation duly pointed out.
    """
    if not artifact.exists():
        return Suite(name=name, how=how, newest_source_at=source_at)

    text = artifact.read_text(encoding="utf-8")
    counters = re.search(r"<Counters\b([^>]*)/>", text)
    finished = re.search(r'<Times\b[^>]*finish="([^"]+)"', text)
    if counters is None:
        return Suite(name=name, how=how, newest_source_at=source_at)

    def attribute(key: str) -> int:
        found = re.search(rf'\b{key}="(\d+)"', counters.group(1))
        return int(found.group(1)) if found else 0

    return Suite(
        name=name,
        how=how,
        passed=attribute("passed"),
        # A TRX has no "skipped" attribute; what did not run is total minus executed.
        skipped=attribute("total") - attribute("executed"),
        failed=attribute("failed") + attribute("error"),
        artifact=artifact,
        ran_at=(
            datetime.fromisoformat(finished.group(1)).astimezone(UTC)
            if finished
            else datetime.fromtimestamp(artifact.stat().st_mtime, UTC)
        ),
        newest_source_at=source_at,
    )


def dotnet_suite(service: str) -> Suite:
    """Newest TRX under the service's test project.

    Source freshness spans `src` AND `tests`, unlike coverage: adding a test file changes the
    count without touching a line of production code, and that is precisely the edit this row
    exists to keep up with.
    """
    how = f"cd backend/{service} && dotnet test {service}.slnx --logger trx"
    tests_root = BACKEND / service / "tests"
    artifact = _newest(list(tests_root.rglob("*.trx"))) if tests_root.exists() else None
    source_at = _newest_mtime(BACKEND / service, ("*.cs",))

    if artifact is None:
        return Suite(name=service, how=how, newest_source_at=source_at)

    return _trx_suite(service, how, artifact, source_at)


def python_suite(service: str) -> Suite:
    return _junit_suite(
        name=service,
        how=f"cd backend/{service} && .venv/bin/python -m pytest --junitxml=test-results.xml",
        artifact=BACKEND / service / "test-results.xml",
        source_at=_newest_mtime(BACKEND / service, ("*.py",)),
    )


def frontend_suite() -> Suite:
    return _junit_suite(
        name="front-end-web",
        how="cd front-end-web && npx vitest run --reporter=junit --outputFile=test-results.xml",
        artifact=REPO / "front-end-web/test-results.xml",
        source_at=_newest_mtime(REPO / "front-end-web/src", ("*.ts", "*.tsx")),
    )


def e2e_suite() -> Suite:
    """The cross-service suite, counted by what actually RAN.

    Only the `-m offline` subset can run here, and this row reports that subset rather than the
    full collection. The distinction is the point: the rest of the suite is authored and has
    never executed, and a table that added it to the others would be claiming those tests pass.
    They may; nothing has asked them.
    """
    return _junit_suite(
        name="Cross-service E2E (offline subset)",
        how="cd backend/tests/e2e && .venv/bin/python -m pytest -m offline --junitxml=test-results.xml",
        artifact=BACKEND / "tests/e2e/test-results.xml",
        source_at=_newest_mtime(BACKEND / "tests/e2e", ("*.py",)),
        note="— the rest of the suite needs the platform; see below",
    )


def all_suites() -> list[Suite]:
    return [
        *(dotnet_suite(service) for service in DOTNET_SERVICES),
        *(python_suite(service) for service in PYTHON_SERVICES),
        frontend_suite(),
        e2e_suite(),
    ]


def inventory_table(suites: list[Suite] | None = None) -> str:
    """§2, generated — the last table in this document that was still typed by hand.

    The total is the part worth being careful about. Summing whichever rows happen to be
    readable produces a smaller number that looks exactly like a real one, so a total is only
    printed when every row can be quoted; otherwise it says how many are missing and why. A
    report that under-counts silently is the same failure as one that over-counts.
    """
    rows = all_suites() if suites is None else suites
    header = "| Suite | Tests | Status | Command |\n|---|---|---|---|"
    lines = [header, *(suite.row() for suite in rows)]

    withheld = [suite for suite in rows if not suite.quotable]
    if withheld:
        lines.append(
            f"| **Total** | — | **incomplete — {len(withheld)} of {len(rows)} suites withheld "
            f"({', '.join(suite.name for suite in withheld)})** | |"
        )
    else:
        total = sum(suite.passed or 0 for suite in rows)
        skipped = sum(suite.skipped for suite in rows)
        lines.append(
            f"| **Total** | **{total:,}** | all {len(rows)} suites passing"
            f"{f', {skipped} skipped' if skipped else ''} | |"
        )

    return "\n".join(lines)
