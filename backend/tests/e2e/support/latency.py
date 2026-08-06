"""Collecting, summarising and judging latency samples (work-plan §9.1/§9.3).

Three decisions are baked in here, and each one is a way the measurement could have
been wrong rather than merely imprecise.

**Percentiles, not means.** A mean hides the case that gets complained about. The
budgets in `docs/latency.md` are stated at p50 and p95 for that reason, and p95 is
what a run passes or fails on.

**The first sample is discarded.** The first message over a fresh connection pays for
a cold DB connection pool and a JIT'd request path. Keeping it does not make the
number conservative, it makes it arbitrary: with twenty samples the cold start would
become the reported `worst` on every single run, and the column would carry no
information at all.

**A breach reports the whole table.** Asserting per-sample would abort the run on the
first slow one, which is exactly the sample you most want to see in context. Every
series is collected, then judged.
"""

from __future__ import annotations

import math
from dataclasses import dataclass, field


@dataclass
class Series:
    """One measured hop: its samples in milliseconds, and what it is allowed to cost."""

    key: str
    description: str
    budget_p50_ms: float
    budget_p95_ms: float
    samples: list[float] = field(default_factory=list)
    # Set when a hop could not be measured at all, with the reason. A skipped hop is
    # reported as skipped; it never silently passes.
    unavailable: str | None = None
    # False for hops whose samples did not come from this harness driving the path —
    # a service's own histogram has no warm-up of ours to discard, and dropping its
    # first observation would just be deleting data.
    discard_warmup: bool = True

    def add(self, seconds: float) -> None:
        self.samples.append(seconds * 1000.0)

    @property
    def measured(self) -> list[float]:
        """Samples excluding the warm-up. See the module docstring."""
        if not self.discard_warmup:
            return self.samples
        return self.samples[1:] if len(self.samples) > 1 else self.samples

    def percentile(self, fraction: float) -> float:
        values = sorted(self.measured)
        if not values:
            return float("nan")
        # Nearest-rank (ceil), not linear interpolation. At these sample sizes p95 is
        # only ever "one of the slowest observed values"; interpolating between two of
        # them reports a figure that was never measured, to a precision 20 samples
        # cannot support. Rounding to a real observation keeps the number honest and
        # keeps it pessimistic, which is the right direction for a budget.
        rank = math.ceil(fraction * len(values))
        return values[max(0, min(len(values) - 1, rank - 1))]

    @property
    def p50(self) -> float:
        return self.percentile(0.50)

    @property
    def p95(self) -> float:
        return self.percentile(0.95)

    @property
    def worst(self) -> float:
        return max(self.measured) if self.measured else float("nan")

    def breaches(self) -> list[str]:
        # Judged on whether there are samples, NOT on whether `unavailable` is set. A
        # hop that collected six samples and then lost the connection has six real
        # observations, and excusing them because the run ended badly would let a hop
        # that was measurably slow report as merely interrupted.
        if not self.measured:
            return []
        failures = []
        if self.p50 > self.budget_p50_ms:
            failures.append(f"{self.key} p50 {self.p50:.0f}ms > {self.budget_p50_ms:.0f}ms")
        if self.p95 > self.budget_p95_ms:
            failures.append(f"{self.key} p95 {self.p95:.0f}ms > {self.budget_p95_ms:.0f}ms")
        return failures

    def row(self) -> str:
        budget = f"{self.budget_p50_ms:.0f} / {self.budget_p95_ms:.0f}"
        if not self.measured:
            note = self.unavailable or "harness collected nothing"
            return f"| {self.key} | {self.description} | not measured | — | — | — | {budget} | {note} |"

        note = f"n={len(self.measured)}"
        if self.discard_warmup:
            note += " (+1 warm-up discarded)"
        if self.unavailable:
            # Partial, and the table has to say so — a p95 over four samples is not the
            # same claim as a p95 over twenty, and the reader cannot tell from the number.
            note += f"; INCOMPLETE — {self.unavailable}"
        verdict = "FAIL" if self.breaches() else "pass"
        return (
            f"| {self.key} | {self.description} | {verdict} | {self.p50:.0f} | {self.p95:.0f} | "
            f"{self.worst:.0f} | {budget} | {note} |"
        )


@dataclass
class Report:
    series: list[Series] = field(default_factory=list)

    def add(self, series: Series) -> Series:
        self.series.append(series)
        return series

    def to_markdown(self) -> str:
        header = (
            "| Hop | What is measured | Verdict | p50 ms | p95 ms | worst ms | budget p50/p95 | notes |\n"
            "|---|---|---|---|---|---|---|---|"
        )
        return "\n".join([header, *(s.row() for s in self.series)])

    def breaches(self) -> list[str]:
        return [breach for s in self.series for breach in s.breaches()]

    def unmeasured(self) -> list[Series]:
        return [s for s in self.series if not s.measured]

    def incomplete(self) -> list[Series]:
        """Series that produced samples but were cut short. Reported separately: they
        are judged against their budgets, but a reader must not take a four-sample p95
        for a twenty-sample one."""
        return [s for s in self.series if s.measured and s.unavailable]
