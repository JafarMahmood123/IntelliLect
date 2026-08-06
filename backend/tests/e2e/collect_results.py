#!/usr/bin/env python3
"""Fill `docs/testing-results.md` from real artifacts (work-plan §10.5).

    cd backend/tests/e2e && .venv/bin/python collect_results.py

Regenerates the machine-derivable half of the results document: coverage per component and per
layer, and the latency table the §9 harness writes. Everything else in that file is prose and is
edited by hand, so this script only rewrites the blocks between the generated markers.

Producing the artifacts first is the caller's job, and deliberately so — a collector that ran the
suites itself would take minutes and would quietly re-run them every time somebody wanted to read
the file. What it will not do is present a number it cannot stand behind: an artifact older than
the code it measures is reported as stale rather than as a percentage.
"""

from __future__ import annotations

import re
import sys
from datetime import UTC, datetime
from pathlib import Path

from support.results import REPO, all_coverage, coverage_table, latency_table, layer_table

TARGET = REPO / "docs/testing-results.md"

BLOCKS = {
    "coverage": lambda: coverage_table(all_coverage()),
    "layers": layer_table,
    "latency": latency_table,
}


def fill(document: str) -> str:
    for name, produce in BLOCKS.items():
        pattern = re.compile(
            rf"(<!-- generated:{name} -->\n).*?(\n<!-- /generated:{name} -->)", re.DOTALL
        )
        if not pattern.search(document):
            raise SystemExit(f"{TARGET} has no <!-- generated:{name} --> block")
        document = pattern.sub(lambda m: m.group(1) + produce() + m.group(2), document)

    return re.sub(
        r"(<!-- generated:stamp -->\n).*?(\n<!-- /generated:stamp -->)",
        lambda m: m.group(1)
        + f"_Generated {datetime.now(UTC):%Y-%m-%d %H:%M UTC} by "
        + "`backend/tests/e2e/collect_results.py` from the artifacts present at that moment._"
        + m.group(2),
        document,
        flags=re.DOTALL,
    )


def main() -> int:
    if not TARGET.exists():
        raise SystemExit(f"{TARGET} does not exist — it is the template, not an output")

    original = TARGET.read_text(encoding="utf-8")
    updated = fill(original)
    TARGET.write_text(updated, encoding="utf-8")

    stale = [row.component for row in all_coverage() if row.stale]
    missing = [row.component for row in all_coverage() if row.line_rate is None]
    print(f"wrote {TARGET.relative_to(REPO)}")
    if stale:
        print(f"  STALE (number withheld): {', '.join(stale)}")
    if missing:
        print(f"  not measured: {', '.join(missing)}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
