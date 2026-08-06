"""Alembic revisions against the shape a migration history has to have (work-plan §11.8).

Applying them needs a real Postgres with pgvector, and that stays integration work. What can be
checked here is the history itself, which is where the failures that hurt actually live: a
divergent head, a broken parent link, or a downgrade that quietly does nothing.

The last one is the reason this file exists. `alembic downgrade` reports **success** when the
downgrade body is empty — so a rollback appears to work, the deployment is declared recovered,
and the schema is still whatever the failed upgrade left behind. P3 in the work plan is an
embedding-dimension change that rewrites the pgvector column; that is exactly the migration
somebody will need to reverse under pressure.

The parsing is static (`ast`), not `alembic.script`: building a `ScriptDirectory` runs `env.py`,
which imports application settings and wants a database URL. A rule that needs the thing it is
checking to be reachable is a rule that will be skipped.
"""

from __future__ import annotations

import ast
from pathlib import Path

import pytest

VERSIONS = Path(__file__).resolve().parents[1] / "alembic" / "versions"


class Revision:
    """One migration file, read without importing it."""

    def __init__(self, path: Path) -> None:
        self.path = path
        tree = ast.parse(path.read_text(encoding="utf-8"))
        self.revision: str | None = None
        self.down_revision: str | None = None
        self.functions: dict[str, ast.FunctionDef] = {}

        for node in tree.body:
            if isinstance(node, ast.FunctionDef):
                self.functions[node.name] = node
            elif isinstance(node, (ast.Assign, ast.AnnAssign)):
                targets = node.targets if isinstance(node, ast.Assign) else [node.target]
                for target in targets:
                    if isinstance(target, ast.Name) and target.id in ("revision", "down_revision"):
                        value = node.value
                        setattr(
                            self,
                            target.id,
                            value.value if isinstance(value, ast.Constant) else None,
                        )

    def body_is_effectively_empty(self, name: str) -> bool:
        """A function whose body is only `pass`, a docstring, or `...`."""
        function = self.functions.get(name)
        if function is None:
            return True
        for statement in function.body:
            if isinstance(statement, ast.Pass):
                continue
            if isinstance(statement, ast.Expr) and isinstance(statement.value, ast.Constant):
                continue  # a docstring or a bare `...`
            return False
        return True


def revisions() -> list[Revision]:
    return [Revision(path) for path in sorted(VERSIONS.glob("[0-9]*.py"))]


def test_there_are_revisions_to_check():
    # Every rule below passes over an empty directory, which is how a conformance test stops
    # meaning anything without ever going red.
    found = revisions()

    assert len(found) >= 1, f"no alembic revisions found under {VERSIONS}"
    assert all(r.revision for r in found), "a revision file declares no revision id"


def test_every_revision_id_is_unique():
    ids = [r.revision for r in revisions()]

    assert len(ids) == len(set(ids)), f"duplicate revision ids: {ids}"


def test_the_history_is_a_single_chain_with_one_head():
    """Two heads is the failure that survives review and breaks on deploy.

    It happens when two branches each add a migration against the same parent. Both files look
    fine on their own, both merge cleanly, and `alembic upgrade head` then refuses with "multiple
    heads" — on the deployment, not in CI, because CI never ran a migration.
    """
    found = revisions()
    ids = {r.revision for r in found}
    parents = {r.down_revision for r in found if r.down_revision is not None}

    roots = [r.revision for r in found if r.down_revision is None]
    heads = sorted(ids - parents)

    assert len(roots) == 1, f"expected exactly one root revision, found {roots}"
    assert len(heads) == 1, f"expected exactly one head, found {heads}"


def test_every_parent_link_points_at_a_revision_that_exists():
    found = revisions()
    ids = {r.revision for r in found}

    dangling = [
        f"{r.path.name} -> {r.down_revision}"
        for r in found
        if r.down_revision is not None and r.down_revision not in ids
    ]

    assert not dangling, f"revisions whose parent is missing: {dangling}"


@pytest.mark.parametrize("revision", revisions(), ids=lambda r: r.path.name)
def test_every_revision_has_an_upgrade_that_does_something(revision: Revision):
    assert not revision.body_is_effectively_empty("upgrade"), (
        f"{revision.path.name} has an empty upgrade — it will be recorded as applied "
        "while changing nothing"
    )


@pytest.mark.parametrize("revision", revisions(), ids=lambda r: r.path.name)
def test_every_revision_can_actually_be_undone(revision: Revision):
    """The one that matters under pressure.

    `alembic downgrade` does not fail on an empty body — it reports success, stamps the previous
    revision, and leaves the schema exactly as the upgrade left it. The rollback then looks
    complete while the database still carries the change that was being backed out.
    """
    assert not revision.body_is_effectively_empty("downgrade"), (
        f"{revision.path.name} cannot be rolled back: its downgrade body is empty, and "
        "`alembic downgrade` will report success anyway"
    )
